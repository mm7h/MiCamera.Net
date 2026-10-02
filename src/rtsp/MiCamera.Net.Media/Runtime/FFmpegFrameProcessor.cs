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
internal unsafe sealed class FFmpegFrameProcessor : IDisposable
{
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
    private bool _disposed;

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
            throw new InvalidOperationException($"FFmpeg does not provide a decoder for {sourceCodec}.");
        }

        this._decoder = ffmpeg.avcodec_alloc_context3(codec);
        this._decodedFrame = ffmpeg.av_frame_alloc();
        this._decodePacket = ffmpeg.av_packet_alloc();
        try
        {
            EnsureAllocated(this._decoder, "decoder context");
            EnsureAllocated(this._decodedFrame, "decoder frame");
            EnsureAllocated(this._decodePacket, "decoder packet");
            this._decoder->thread_count = 1;
            ThrowIfError(ffmpeg.avcodec_open2(this._decoder, codec, null), "open decoder");
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
            error = FFmpegRuntime.Failure ?? new InvalidOperationException("FFmpeg native libraries are unavailable.");
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

    public ProcessResult Process(VideoAccessUnit unit, bool transcodeToH264)
    {
        ObjectDisposedException.ThrowIf(this._disposed, this);
        ArgumentOutOfRangeException.ThrowIfZero(unit.AnnexB.Length);

        ffmpeg.av_packet_unref(this._decodePacket);
        ThrowIfError(ffmpeg.av_new_packet(this._decodePacket, unit.AnnexB.Length), "allocate decode packet");
        Marshal.Copy(unit.AnnexB.ToArray(), 0, (IntPtr)this._decodePacket->data, unit.AnnexB.Length);
        this._decodePacket->pts = unit.Timestamp90Khz;
        this._decodePacket->dts = unit.Timestamp90Khz;
        ThrowIfError(ffmpeg.avcodec_send_packet(this._decoder, this._decodePacket), "decode video packet");

        VideoSnapshot? snapshot = null;
        List<VideoAccessUnit> transcoded = [];

        while (ffmpeg.avcodec_receive_frame(this._decoder, this._decodedFrame) == 0)
        {
            if (unit.IsKeyFrame)
            {
                snapshot = this.EncodeJpeg(unit);
            }

            if (transcodeToH264)
            {
                transcoded.AddRange(this.EncodeH264(unit));
            }
        }

        return new ProcessResult(snapshot, transcoded);
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

    private VideoSnapshot? EncodeJpeg(VideoAccessUnit unit)
    {
        this.EnsureEncoders();
        this.CopyDecodedFrameTo(this._jpegFrame, this._jpegConverter, unit.Timestamp90Khz);

        ffmpeg.av_packet_unref(this._jpegPacket);
        ThrowIfError(ffmpeg.avcodec_send_frame(this._jpegEncoder, this._jpegFrame), "encode JPEG frame");
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
            unit.SourceSequence,
            this._width,
            this._height,
            this._sourceCodec);
    }

    private IReadOnlyList<VideoAccessUnit> EncodeH264(VideoAccessUnit source)
    {
        this.EnsureEncoders();
        this.EnsureH264Encoder();
        this.CopyDecodedFrameTo(this._h264Frame, this._h264Converter, source.Timestamp90Khz);

        ffmpeg.av_packet_unref(this._h264Packet);
        ThrowIfError(ffmpeg.avcodec_send_frame(this._h264Encoder, this._h264Frame), "encode H.264 frame");

        List<VideoAccessUnit> result = [];
        while (ffmpeg.avcodec_receive_packet(this._h264Encoder, this._h264Packet) == 0)
        {
            byte[] annexB = new byte[this._h264Packet->size];
            Marshal.Copy((IntPtr)this._h264Packet->data, annexB, 0, annexB.Length);
            bool isKeyFrame = AnnexBBitstream.IsKeyFrame(VideoCodec.H264, annexB);
            bool hasParameters = AnnexBBitstream.ContainsCodecParameters(VideoCodec.H264, annexB);
            result.Add(new VideoAccessUnit(
                this._streamId,
                VideoCodec.H264,
                annexB,
                source.SourceSequence,
                source.Timestamp90Khz,
                this._frameDuration,
                isKeyFrame,
                hasParameters));
        }

        return result;
    }

    private void EnsureEncoders()
    {
        if (this._width == this._decodedFrame->width && this._height == this._decodedFrame->height && this._jpegFrame is not null)
        {
            return;
        }

        if (this._decodedFrame->width <= 0 || this._decodedFrame->height <= 0)
        {
            throw new InvalidOperationException("FFmpeg produced a frame without dimensions.");
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
        this._jpegFrame = this.CreateConversionFrame(AVPixelFormat.AV_PIX_FMT_YUVJ420P, "JPEG conversion frame");
        this._jpegConverter = this.CreateConverter(AVPixelFormat.AV_PIX_FMT_YUVJ420P, "JPEG pixel converter");

        AVCodec* jpegCodec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_MJPEG);
        EnsureAllocated(jpegCodec, "MJPEG encoder");
        this._jpegEncoder = ffmpeg.avcodec_alloc_context3(jpegCodec);
        EnsureAllocated(this._jpegEncoder, "MJPEG encoder context");
        this.ConfigureVideoEncoder(this._jpegEncoder, AVPixelFormat.AV_PIX_FMT_YUVJ420P);
        this._jpegEncoder->qmin = Math.Clamp(31 - (this._jpegQuality * 30 / 100), 1, 31);
        this._jpegEncoder->qmax = this._jpegEncoder->qmin;
        ThrowIfError(ffmpeg.avcodec_open2(this._jpegEncoder, jpegCodec, null), "open MJPEG encoder");
        this._jpegPacket = ffmpeg.av_packet_alloc();
        EnsureAllocated(this._jpegPacket, "MJPEG packet");
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

        EnsureAllocated(codec, $"H.264 encoder '{this._options.H264EncoderName}'");
        this._h264Encoder = ffmpeg.avcodec_alloc_context3(codec);
        EnsureAllocated(this._h264Encoder, "H.264 encoder context");
        if (this._h264Frame is null) this._h264Frame = this.CreateConversionFrame(AVPixelFormat.AV_PIX_FMT_YUV420P, "H.264 conversion frame");
        if (this._h264Converter is null) this._h264Converter = this.CreateConverter(AVPixelFormat.AV_PIX_FMT_YUV420P, "H.264 pixel converter");
        this.ConfigureVideoEncoder(this._h264Encoder, AVPixelFormat.AV_PIX_FMT_YUV420P);
        this._h264Encoder->bit_rate = this._options.H264Bitrate;
        this._h264Encoder->gop_size = Math.Max(1, (int)Math.Round(90_000d / this._frameDuration * this._options.KeyFrameInterval.TotalSeconds));
        this._h264Encoder->max_b_frames = 0;
        _ = ffmpeg.av_opt_set(this._h264Encoder->priv_data, "preset", this._options.H264Preset, 0);
        _ = ffmpeg.av_opt_set(this._h264Encoder->priv_data, "tune", "zerolatency", 0);
        _ = ffmpeg.av_opt_set(this._h264Encoder->priv_data, "profile", "baseline", 0);
        _ = ffmpeg.av_opt_set(this._h264Encoder->priv_data, "annexb", "1", 0);
        _ = ffmpeg.av_opt_set(this._h264Encoder->priv_data, "repeat-headers", "1", 0);
        ThrowIfError(ffmpeg.avcodec_open2(this._h264Encoder, codec, null), "open H.264 encoder");
        this._h264Packet = ffmpeg.av_packet_alloc();
        EnsureAllocated(this._h264Packet, "H.264 packet");
    }

    private void ConfigureVideoEncoder(AVCodecContext* context, AVPixelFormat pixelFormat)
    {
        context->width = this._width;
        context->height = this._height;
        context->pix_fmt = pixelFormat;
        context->time_base = new AVRational { num = (int)this._frameDuration, den = 90_000 };
        context->framerate = new AVRational { num = 90_000, den = (int)this._frameDuration };
    }

    private AVFrame* CreateConversionFrame(AVPixelFormat pixelFormat, string name)
    {
        AVFrame* frame = ffmpeg.av_frame_alloc();
        EnsureAllocated(frame, name);
        frame->format = (int)pixelFormat;
        frame->width = this._width;
        frame->height = this._height;
        ThrowIfError(ffmpeg.av_frame_get_buffer(frame, 32), $"allocate {name}");
        return frame;
    }

    private SwsContext* CreateConverter(AVPixelFormat destinationFormat, string name)
    {
        SwsContext* converter = ffmpeg.sws_getCachedContext(
            null,
            this._width,
            this._height,
            (AVPixelFormat)this._decodedFrame->format,
            this._width,
            this._height,
            destinationFormat,
            ffmpeg.SWS_BILINEAR,
            null,
            null,
            null);
        EnsureAllocated(converter, name);
        return converter;
    }

    private void CopyDecodedFrameTo(AVFrame* target, SwsContext* converter, uint timestamp90Khz)
    {
        ThrowIfError(ffmpeg.av_frame_make_writable(target), "make conversion frame writable");
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

    private static void ThrowIfError(int error, string operation)
    {
        if (error < 0)
        {
            throw new InvalidOperationException($"FFmpeg failed to {operation} (error {error}).");
        }
    }

    private static void EnsureAllocated(void* pointer, string name)
    {
        if (pointer is null)
        {
            throw new InvalidOperationException($"FFmpeg could not allocate {name}.");
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
