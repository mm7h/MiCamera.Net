using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using MiCamera.Net.Abstractions.Common.Enums;
using MiCamera.Net.Media.Services;
using MiCamera.Net.RTSP.Abstractions.ConfigSettings;
using MiCamera.Net.RTSP.Abstractions.Media;

namespace MiCamera.Net.Media.Runtime;

/// <summary>
/// Owns one sequential FFmpeg decoder and optional H.264 encoder for a camera stream.
/// FFmpeg contexts are deliberately never shared between stream workers.
/// </summary>
internal sealed unsafe class FFmpegFrameProcessor : IDisposable
{
    /// <summary>AV_FRAME_FLAG_KEY from libavutil/frame.h, which these bindings do not expose.</summary>
    private const int KeyFrameFlag = 1 << 1;

    /// <summary>
    /// Never let key frames dominate the stream: a host that encodes below the nominal frame
    /// rate would otherwise turn every frame into an expensive intra frame and slow down further.
    /// </summary>
    private const int MinimumFramesBetweenKeyFrames = 8;

    private readonly string _streamId;
    private readonly VideoCodec _sourceCodec;
    private readonly uint _frameDuration;
    private readonly MediaProcessingOptions _options;
    private readonly int _jpegQuality;
    private AVCodecContext* _decoder;
    private AVFrame* _decodedFrame;
    private AVPacket* _decodePacket;
    private AVCodecContext* _jpegEncoder;
    private AVPacket* _jpegPacket;
    private AVCodecContext* _h264Encoder;
    private AVPacket* _h264Packet;
    private AVFrame* _jpegFrame;
    private SwsContext* _jpegConverter;
    private AVFrame* _h264Frame;
    private SwsContext* _h264Converter;
    private int _width;
    private int _height;
    private int _h264Width;
    private int _h264Height;
    private long _lastKeyFrameTicks;
    private long _framesSinceKeyFrame;
    private volatile bool _keyFrameRequested;
    private bool _decodingInterFrames;
    private uint? _previousTimestamp;
    private long _timestamp90Khz;
    /// <summary>
    /// Maps the presentation timestamp of every submitted packet to the source access unit it came
    /// from. Frame-level decoder threads hold several frames back, so the frame that leaves the
    /// decoder is not the packet that was just submitted; its PTS is the only reliable link back to
    /// the source sequence and RTP timestamp.
    /// </summary>
    private readonly Dictionary<long, SourceOrigin> _origins = [];
    private readonly Queue<long> _originOrder = new();
    private bool _disposed;

    private static readonly ProcessResult s_empty = new(null, []);

    private FFmpegFrameProcessor(
        string streamId,
        VideoCodec sourceCodec,
        double nominalFrameRate,
        MediaProcessingOptions options,
        int jpegQuality)
    {
        this._streamId = streamId;
        this._sourceCodec = sourceCodec;
        this._frameDuration = (uint)Math.Max(1, Math.Round(90_000d / nominalFrameRate));
        this._options = options;
        this._jpegQuality = jpegQuality;

        AVCodecID codecId = sourceCodec == VideoCodec.H264 ? AVCodecID.AV_CODEC_ID_H264 : AVCodecID.AV_CODEC_ID_HEVC;
        AVCodec* codec = ffmpeg.avcodec_find_decoder(codecId);
        if (codec is null)
        {
            throw new InvalidOperationException($"FFmpeg 未提供 {sourceCodec} 解码器。");
        }

        this._decoder = ffmpeg.avcodec_alloc_context3(codec);
        this._decodedFrame = ffmpeg.av_frame_alloc();
        this._decodePacket = ffmpeg.av_packet_alloc();
        try
        {
            EnsureAllocated(this._decoder, "解码器上下文");
            EnsureAllocated(this._decodedFrame, "解码帧");
            EnsureAllocated(this._decodePacket, "解码数据包");
            // Slice threads alone leave a 4K HEVC decoder at roughly half the throughput of frame
            // threads on this class of CPU, so both modes are enabled and every core participates.
            this._decoder->thread_count = Math.Max(1, Environment.ProcessorCount);
            this._decoder->thread_type = ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE;
            ThrowIfError(ffmpeg.avcodec_open2(this._decoder, codec, null), "打开解码器");
        }
        catch
        {
            this.Dispose();
            throw;
        }
    }

    public static bool TryCreate(
        string streamId,
        VideoCodec sourceCodec,
        double nominalFrameRate,
        MediaProcessingOptions options,
        int jpegQuality,
        out FFmpegFrameProcessor? processor,
        out Exception? error)
    {
        processor = null;
        error = null;

        if (!FFmpegRuntime.IsAvailable)
        {
            error = FFmpegRuntime.Failure ?? new InvalidOperationException("FFmpeg 原生库不可用。");
            return false;
        }

        try
        {
            processor = new FFmpegFrameProcessor(streamId, sourceCodec, nominalFrameRate, options, jpegQuality);
            return true;
        }
        catch (Exception exception)
        {
            error = exception;
            processor?.Dispose();
            processor = null;
            return false;
        }
    }

    public ProcessResult Process(VideoAccessUnit unit, bool transcodeToH264, bool snapshotWanted)
    {
        ObjectDisposedException.ThrowIf(this._disposed, this);
        ArgumentOutOfRangeException.ThrowIfZero(unit.AnnexB.Length);

        // RTP wraps after about 13 hours; native encoder PTS must remain increasing. Skipped units
        // still advance the clock so a decoding gap keeps its real source time span.
        this._timestamp90Khz = this._previousTimestamp is { } previous
            ? this._timestamp90Khz + unchecked(unit.Timestamp90Khz - previous)
            : unit.Timestamp90Khz;
        this._previousTimestamp = unit.Timestamp90Khz;

        // Resume full decoding at a key frame so references discarded while idle cannot corrupt the
        // first frames of a new preview.
        bool transcode = transcodeToH264 && (this._decodingInterFrames || unit.IsKeyFrame);
        bool snapshot = snapshotWanted && unit.IsKeyFrame;

        if (this._decodingInterFrames && !transcode)
        {
            // The last viewer left. Drop the frames the decoder still holds so a later viewer never
            // receives the stale tail of the previous session.
            this.FlushDecoder();
        }

        this._decodingInterFrames = transcode;

        // An inter frame cannot be decoded without its references, and a snapshot only needs key
        // frames, so submitting it would only burn CPU that the live encoders need.
        if (!transcode && !snapshot)
        {
            return s_empty;
        }

        long packetTimestamp = this._timestamp90Khz;
        ffmpeg.av_packet_unref(this._decodePacket);
        ThrowIfError(ffmpeg.av_new_packet(this._decodePacket, unit.AnnexB.Length), "分配解码数据包");
        unit.AnnexB.Span.CopyTo(new Span<byte>(this._decodePacket->data, unit.AnnexB.Length));
        this._decodePacket->pts = packetTimestamp;
        this._decodePacket->dts = packetTimestamp;
        this.QueueOrigin(packetTimestamp, unit);
        ThrowIfError(ffmpeg.avcodec_send_packet(this._decoder, this._decodePacket), "解码视频数据包");

        VideoSnapshot? jpeg = null;
        List<VideoAccessUnit> transcoded = [];

        // Frame threads delay output until more packets arrive. An idle screenshot has no
        // following packets to unlock it, so drain this independent key frame immediately.
        if (!transcode)
        {
            ThrowIfError(ffmpeg.avcodec_send_packet(this._decoder, null), "排空快照解码器");
        }

        while (ffmpeg.avcodec_receive_frame(this._decoder, this._decodedFrame) == 0)
        {
            SourceOrigin origin = this.TakeOrigin(this._decodedFrame->pts, unit);
            bool frameIsKeyFrame = (this._decodedFrame->flags & KeyFrameFlag) != 0 ||
                this._decodedFrame->pict_type == AVPictureType.AV_PICTURE_TYPE_I;

            if (snapshotWanted && jpeg is null && frameIsKeyFrame)
            {
                jpeg = this.EncodeJpeg(origin);
            }

            if (transcode)
            {
                transcoded.AddRange(this.EncodeH264(origin));
            }
        }

        if (!transcode)
        {
            this.FlushDecoder();
        }

        return new ProcessResult(jpeg, transcoded);
    }

    /// <summary>
    /// Forces the next encoded frame to be a key frame so a new WebRTC viewer does not have to
    /// wait for the encoder's own key frame cadence.
    /// </summary>
    public void RequestKeyFrame()
    {
        this._keyFrameRequested = true;
    }

    public void StopH264Transcoding()
    {
        ObjectDisposedException.ThrowIf(this._disposed, this);
        FreePacket(ref this._h264Packet);
        FreeCodecContext(ref this._h264Encoder);
    }

    public void Dispose()
    {
        if (this._disposed)
        {
            return;
        }

        this._disposed = true;

        if (this._jpegConverter is not null)
        {
            ffmpeg.sws_freeContext(this._jpegConverter);
            this._jpegConverter = null;
        }

        if (this._h264Converter is not null)
        {
            ffmpeg.sws_freeContext(this._h264Converter);
            this._h264Converter = null;
        }

        FreeFrame(ref this._jpegFrame);
        FreeFrame(ref this._h264Frame);
        FreePacket(ref this._h264Packet);
        FreeCodecContext(ref this._h264Encoder);
        FreePacket(ref this._jpegPacket);
        FreeCodecContext(ref this._jpegEncoder);
        FreePacket(ref this._decodePacket);
        FreeFrame(ref this._decodedFrame);
        FreeCodecContext(ref this._decoder);
    }

    private VideoSnapshot? EncodeJpeg(SourceOrigin origin)
    {
        this.EnsureEncoders();
        this.CopyDecodedFrameTo(this._jpegFrame, this._jpegConverter, this._decodedFrame->pts);

        ffmpeg.av_packet_unref(this._jpegPacket);
        ThrowIfError(ffmpeg.avcodec_send_frame(this._jpegEncoder, this._jpegFrame), "编码 JPEG 帧");
        if (ffmpeg.avcodec_receive_packet(this._jpegEncoder, this._jpegPacket) != 0)
        {
            return null;
        }

        byte[] jpeg = new byte[this._jpegPacket->size];
        Marshal.Copy((IntPtr)this._jpegPacket->data, jpeg, 0, jpeg.Length);
        return new VideoSnapshot(
            this._streamId,
            jpeg,
            DateTimeOffset.UtcNow,
            origin.Sequence,
            this._width,
            this._height,
            this._sourceCodec);
    }

    private IReadOnlyList<VideoAccessUnit> EncodeH264(SourceOrigin origin)
    {
        this.EnsureEncoders();
        this.EnsureH264Encoder();
        if (this._decodedFrame->format == (int)AVPixelFormat.AV_PIX_FMT_YUV420P &&
            this._width == this._h264Width && this._height == this._h264Height)
        {
            // Native-resolution HEVC already has the encoder's pixel layout. Keep a reference
            // instead of copying every 4K frame through swscale; only our frame metadata changes.
            ffmpeg.av_frame_unref(this._h264Frame);
            ThrowIfError(ffmpeg.av_frame_ref(this._h264Frame, this._decodedFrame), "引用 H.264 输入帧");
        }
        else
        {
            this.CopyDecodedFrameTo(this._h264Frame, this._h264Converter, this._decodedFrame->pts);
        }
        this.ApplyKeyFrameRequest();

        ffmpeg.av_packet_unref(this._h264Packet);
        ThrowIfError(ffmpeg.avcodec_send_frame(this._h264Encoder, this._h264Frame), "编码 H.264 帧");

        List<VideoAccessUnit> result = [];
        while (ffmpeg.avcodec_receive_packet(this._h264Encoder, this._h264Packet) == 0)
        {
            byte[] annexB = new byte[this._h264Packet->size];
            Marshal.Copy((IntPtr)this._h264Packet->data, annexB, 0, annexB.Length);
            result.Add(new VideoAccessUnit(
                this._streamId,
                VideoCodec.H264,
                annexB,
                origin.Sequence,
                origin.Timestamp90Khz,
                origin.Duration90Khz,
                AnnexBBitstream.IsKeyFrame(VideoCodec.H264, annexB),
                AnnexBBitstream.ContainsCodecParameters(VideoCodec.H264, annexB)));
        }

        return result;
    }

    /// <summary>
    /// Remembers which source access unit a submitted packet belongs to. Frame-level decoder
    /// threads return frames several packets later, so the mapping cannot be assumed away.
    /// </summary>
    private void QueueOrigin(long packetTimestamp, VideoAccessUnit unit)
    {
        this._origins[packetTimestamp] = new SourceOrigin(unit.SourceSequence, unit.Timestamp90Khz, unit.Duration90Khz);
        this._originOrder.Enqueue(packetTimestamp);

        while (this._originOrder.Count > 256)
        {
            this._origins.Remove(this._originOrder.Dequeue());
        }
    }

    /// <summary>
    /// Resolves the source access unit behind a decoded frame and drops every older mapping, which
    /// keeps the queue bounded by the decoder's frame-thread depth.
    /// </summary>
    private SourceOrigin TakeOrigin(long frameTimestamp, VideoAccessUnit fallback)
    {
        if (!this._origins.Remove(frameTimestamp, out SourceOrigin origin))
        {
            origin = new SourceOrigin(fallback.SourceSequence, fallback.Timestamp90Khz, fallback.Duration90Khz);
        }

        while (this._originOrder.TryPeek(out long queued) && queued <= frameTimestamp)
        {
            this._originOrder.Dequeue();
            this._origins.Remove(queued);
        }

        return origin;
    }

    private void FlushDecoder()
    {
        ffmpeg.avcodec_flush_buffers(this._decoder);
        this._origins.Clear();
        this._originOrder.Clear();
    }

    /// <summary>
    /// Keeps key frames bounded by wall-clock time instead of only by encoded frame count.
    /// A host that cannot reach the nominal frame rate would otherwise leave a viewer without
    /// a usable random access point for many seconds, which looks like a frozen preview.
    /// </summary>
    private void ApplyKeyFrameRequest()
    {
        long now = Environment.TickCount64;
        long interval = (long)this._options.KeyFrameInterval.TotalMilliseconds;
        this._framesSinceKeyFrame++;
        bool due = this._keyFrameRequested
            || (interval > 0
                && this._framesSinceKeyFrame >= MinimumFramesBetweenKeyFrames
                && now - this._lastKeyFrameTicks >= interval);

        this._h264Frame->pict_type = due ? AVPictureType.AV_PICTURE_TYPE_I : AVPictureType.AV_PICTURE_TYPE_NONE;
        if (due)
        {
            this._h264Frame->flags |= KeyFrameFlag;
            this._lastKeyFrameTicks = now;
            this._framesSinceKeyFrame = 0;
            this._keyFrameRequested = false;
        }
        else
        {
            this._h264Frame->flags &= ~KeyFrameFlag;
        }
    }

    private void EnsureEncoders()
    {
        if (this._width == this._decodedFrame->width && this._height == this._decodedFrame->height && this._jpegFrame is not null)
        {
            return;
        }

        if (this._decodedFrame->width <= 0 || this._decodedFrame->height <= 0)
        {
            throw new InvalidOperationException("FFmpeg 生成的帧缺少尺寸信息。");
        }

        if (this._jpegConverter is not null)
        {
            ffmpeg.sws_freeContext(this._jpegConverter);
            this._jpegConverter = null;
        }

        if (this._h264Converter is not null)
        {
            ffmpeg.sws_freeContext(this._h264Converter);
            this._h264Converter = null;
        }

        FreeFrame(ref this._jpegFrame);
        FreeFrame(ref this._h264Frame);
        FreePacket(ref this._jpegPacket);
        FreeCodecContext(ref this._jpegEncoder);
        FreePacket(ref this._h264Packet);
        FreeCodecContext(ref this._h264Encoder);

        this._width = this._decodedFrame->width;
        this._height = this._decodedFrame->height;
        (this._h264Width, this._h264Height) = GetH264OutputSize(this._width, this._height, this._options);
        this._jpegFrame = this.CreateConversionFrame(AVPixelFormat.AV_PIX_FMT_YUVJ420P, this._width, this._height, "JPEG 转换帧");
        this._jpegConverter = this.CreateConverter(AVPixelFormat.AV_PIX_FMT_YUVJ420P, this._width, this._height, ffmpeg.SWS_BILINEAR, "JPEG 像素转换器");

        AVCodec* jpegCodec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_MJPEG);
        EnsureAllocated(jpegCodec, "MJPEG 编码器");
        this._jpegEncoder = ffmpeg.avcodec_alloc_context3(jpegCodec);
        EnsureAllocated(this._jpegEncoder, "MJPEG 编码器上下文");
        this.ConfigureVideoEncoder(this._jpegEncoder, AVPixelFormat.AV_PIX_FMT_YUVJ420P, this._width, this._height);
        // MJPEG supports slice threads: parallelize a 4K snapshot without frame-thread
        // buffering, which would delay the first snapshot until another key frame.
        this._jpegEncoder->thread_count = Math.Min(2, Environment.ProcessorCount);
        this._jpegEncoder->thread_type = ffmpeg.FF_THREAD_SLICE;
        this._jpegEncoder->qmin = Math.Clamp(31 - (this._jpegQuality * 30 / 100), 1, 31);
        this._jpegEncoder->qmax = this._jpegEncoder->qmin;
        ThrowIfError(ffmpeg.avcodec_open2(this._jpegEncoder, jpegCodec, null), "打开 MJPEG 编码器");
        this._jpegPacket = ffmpeg.av_packet_alloc();
        EnsureAllocated(this._jpegPacket, "MJPEG 数据包");
    }

    private void EnsureH264Encoder()
    {
        if (this._h264Encoder is not null)
        {
            return;
        }

        AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name(this._options.H264EncoderName);
        if (codec is null)
        {
            codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_H264);
        }

        EnsureAllocated(codec, $"H.264 编码器“{this._options.H264EncoderName}”");
        this._h264Encoder = ffmpeg.avcodec_alloc_context3(codec);
        EnsureAllocated(this._h264Encoder, "H.264 编码器上下文");
        if (this._h264Frame is null)
        {
            this._h264Frame = this.CreateConversionFrame(AVPixelFormat.AV_PIX_FMT_YUV420P, this._h264Width, this._h264Height, "H.264 转换帧");
        }

        if (this._h264Converter is null)
        {
            this._h264Converter = this.CreateConverter(AVPixelFormat.AV_PIX_FMT_YUV420P, this._h264Width, this._h264Height, ffmpeg.SWS_FAST_BILINEAR, "H.264 像素转换器");
        }

        this.ConfigureVideoEncoder(this._h264Encoder, AVPixelFormat.AV_PIX_FMT_YUV420P, this._h264Width, this._h264Height);
        this._h264Encoder->thread_count = Math.Max(1, Environment.ProcessorCount);
        this._h264Encoder->bit_rate = this._options.H264Bitrate;
        // Bound key-frame bursts to a quarter second of bitrate so a new viewer can
        // receive a complete IDR without overflowing UDP/reassembly buffers.
        this._h264Encoder->rc_max_rate = this._options.H264Bitrate;
        this._h264Encoder->rc_buffer_size = Math.Max(1, this._options.H264Bitrate / 4);
        this._h264Encoder->gop_size = Math.Max(1, (int)Math.Round(90_000d / this._frameDuration * this._options.KeyFrameInterval.TotalSeconds));
        this._h264Encoder->max_b_frames = 0;
        _ = ffmpeg.av_opt_set(this._h264Encoder->priv_data, "preset", this._options.H264Preset, 0);
        _ = ffmpeg.av_opt_set(this._h264Encoder->priv_data, "tune", "zerolatency", 0);
        _ = ffmpeg.av_opt_set(this._h264Encoder->priv_data, "profile", "baseline", 0);
        _ = ffmpeg.av_opt_set(this._h264Encoder->priv_data, "annexb", "1", 0);
        _ = ffmpeg.av_opt_set(this._h264Encoder->priv_data, "repeat-headers", "1", 0);
        _ = ffmpeg.av_opt_set(this._h264Encoder->priv_data, "forced-idr", "1", 0);
        ThrowIfError(ffmpeg.avcodec_open2(this._h264Encoder, codec, null), "打开 H.264 编码器");
        this._h264Packet = ffmpeg.av_packet_alloc();
        EnsureAllocated(this._h264Packet, "H.264 数据包");
    }

    private void ConfigureVideoEncoder(AVCodecContext* context, AVPixelFormat pixelFormat, int width, int height)
    {
        context->width = width;
        context->height = height;
        context->pix_fmt = pixelFormat;
        context->time_base = new AVRational { num = 1, den = 90_000 };
        context->framerate = new AVRational { num = 90_000, den = (int)this._frameDuration };
        context->thread_count = 1;
    }

    private AVFrame* CreateConversionFrame(AVPixelFormat pixelFormat, int width, int height, string name)
    {
        AVFrame* frame = ffmpeg.av_frame_alloc();
        EnsureAllocated(frame, name);
        frame->format = (int)pixelFormat;
        frame->width = width;
        frame->height = height;
        ThrowIfError(ffmpeg.av_frame_get_buffer(frame, 32), $"分配{name}");
        return frame;
    }

    private SwsContext* CreateConverter(AVPixelFormat destinationFormat, int width, int height, int flags, string name)
    {
        SwsContext* converter = ffmpeg.sws_getCachedContext(
            null,
            this._width,
            this._height,
            (AVPixelFormat)this._decodedFrame->format,
            width,
            height,
            destinationFormat,
            flags,
            null,
            null,
            null);
        EnsureAllocated(converter, name);
        return converter;
    }

    private void CopyDecodedFrameTo(AVFrame* target, SwsContext* converter, long timestamp90Khz)
    {
        ThrowIfError(ffmpeg.av_frame_make_writable(target), "将转换帧设为可写");
        _ = ffmpeg.sws_scale(
            converter,
            this._decodedFrame->data,
            this._decodedFrame->linesize,
            0,
            this._height,
            target->data,
            target->linesize);
        target->pts = timestamp90Khz;
    }

    internal static (int Width, int Height) GetH264OutputSize(int sourceWidth, int sourceHeight, MediaProcessingOptions options)
    {
        int maxWidth = options.H264MaxWidth;
        int maxHeight = options.H264MaxHeight;
        if (maxWidth <= 0 || maxHeight <= 0 || (sourceWidth <= maxWidth && sourceHeight <= maxHeight))
        {
            return (sourceWidth, sourceHeight);
        }

        double scale = Math.Min((double)maxWidth / sourceWidth, (double)maxHeight / sourceHeight);
        return (
            MakeEven((int)Math.Round(sourceWidth * scale)),
            MakeEven((int)Math.Round(sourceHeight * scale)));
    }

    private static int MakeEven(int value)
    {
        int even = value % 2 == 0 ? value : value - 1;
        return Math.Max(2, even);
    }

    private static void ThrowIfError(int error, string operation)
    {
        if (error < 0)
        {
            throw new InvalidOperationException($"FFmpeg 操作失败：{operation}（错误码 {error}）。");
        }
    }

    private static void EnsureAllocated(void* pointer, string name)
    {
        if (pointer is null)
        {
            throw new InvalidOperationException($"FFmpeg 无法分配{name}。");
        }
    }

    private static void FreeCodecContext(ref AVCodecContext* context)
    {
        if (context is not null)
        {
            AVCodecContext* value = context;
            ffmpeg.avcodec_free_context(&value);
            context = value;
        }
    }

    private static void FreeFrame(ref AVFrame* frame)
    {
        if (frame is not null)
        {
            AVFrame* value = frame;
            ffmpeg.av_frame_free(&value);
            frame = value;
        }
    }

    private static void FreePacket(ref AVPacket* packet)
    {
        if (packet is not null)
        {
            AVPacket* value = packet;
            ffmpeg.av_packet_free(&value);
            packet = value;
        }
    }
}
