using MiCamera.Net.Abstractions.Common.Enums;

namespace MiCamera.Net.Media.Services;

public static class AnnexBBitstream
{
    public static IReadOnlyList<ReadOnlyMemory<byte>> SplitNalUnits(ReadOnlyMemory<byte> annexB)
    {
        ReadOnlySpan<byte> data = annexB.Span;
        List<ReadOnlyMemory<byte>> result = [];
        int searchStart = 0;

        while (TryFindStartCode(data, searchStart, out int start, out int startCodeLength))
        {
            int nalStart = start + startCodeLength;
            if (!TryFindStartCode(data, nalStart, out int nextStart, out _))
            {
                nextStart = data.Length;
            }

            if (nextStart > nalStart)
            {
                result.Add(annexB[nalStart..nextStart]);
            }

            if (nextStart >= data.Length)
            {
                break;
            }

            searchStart = nextStart;
        }

        return result;
    }

    public static bool IsKeyFrame(VideoCodec codec, ReadOnlySpan<byte> annexB)
    {
        return SplitNalUnits(annexB.ToArray()).Any(nal => IsKeyFrameNal(codec, nal.Span));
    }

    public static bool ContainsCodecParameters(VideoCodec codec, ReadOnlySpan<byte> annexB)
    {
        return SplitNalUnits(annexB.ToArray()).Any(nal => IsCodecParameterNal(codec, nal.Span));
    }

    public static bool IsKeyFrameNal(VideoCodec codec, ReadOnlySpan<byte> nal)
    {
        if (nal.IsEmpty)
        {
            return false;
        }

        int type = GetNalType(codec, nal);
        return codec == VideoCodec.H264 ? type == 5 : type is >= 16 and <= 21;
    }

    public static bool IsCodecParameterNal(VideoCodec codec, ReadOnlySpan<byte> nal)
    {
        if (nal.IsEmpty)
        {
            return false;
        }

        int type = GetNalType(codec, nal);
        return codec == VideoCodec.H264 ? type is 7 or 8 : type is 32 or 33 or 34;
    }

    public static int GetNalType(VideoCodec codec, ReadOnlySpan<byte> nal)
    {
        return codec == VideoCodec.H264 ? nal[0] & 0x1f : (nal[0] >> 1) & 0x3f;
    }

    public static byte[] AddStartCode(ReadOnlySpan<byte> nal)
    {
        byte[] result = new byte[nal.Length + 4];
        result[3] = 1;
        nal.CopyTo(result.AsSpan(4));
        return result;
    }

    public static byte[] CombineWithStartCodes(IEnumerable<ReadOnlyMemory<byte>> nals)
    {
        IReadOnlyList<ReadOnlyMemory<byte>> items = [.. nals.Where(static nal => !nal.IsEmpty)];
        int length = items.Sum(static nal => nal.Length + 4);
        byte[] result = new byte[length];
        int offset = 0;

        foreach (ReadOnlyMemory<byte> nal in items)
        {
            result[offset + 3] = 1;
            nal.Span.CopyTo(result.AsSpan(offset + 4));
            offset += nal.Length + 4;
        }

        return result;
    }

    private static bool TryFindStartCode(
        ReadOnlySpan<byte> data,
        int startAt,
        out int startCodeOffset,
        out int startCodeLength)
    {
        for (int index = startAt; index <= data.Length - 3; index++)
        {
            if (data[index] != 0 || data[index + 1] != 0)
            {
                continue;
            }

            if (data[index + 2] == 1)
            {
                startCodeOffset = index;
                startCodeLength = 3;
                return true;
            }

            if (index <= data.Length - 4 && data[index + 2] == 0 && data[index + 3] == 1)
            {
                startCodeOffset = index;
                startCodeLength = 4;
                return true;
            }
        }

        startCodeOffset = 0;
        startCodeLength = 0;
        return false;
    }
}
