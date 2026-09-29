using MiCamera.Net.Abstractions.Common.Enums;

namespace MiCamera.Net.Server.Common;

internal static class AnnexBBitstreamInspector
{
    public static BitstreamInspection Inspect(ReadOnlySpan<byte> data, VideoCodec codec)
    {
        bool isKeyFrame = false;
        bool containsCodecParameters = false;
        int offset = 0;

        while (TryFindStartCode(data, offset, out int startCodeOffset, out int startCodeLength))
        {
            int nalOffset = startCodeOffset + startCodeLength;
            if (nalOffset >= data.Length)
            {
                break;
            }

            byte header = data[nalOffset];

            if (codec == VideoCodec.H264)
            {
                int nalType = header & 0x1F;
                isKeyFrame |= nalType == 5;
                containsCodecParameters |= nalType is 7 or 8;
            }
            else
            {
                int nalType = (header >> 1) & 0x3F;
                isKeyFrame |= nalType is >= 16 and <= 21;
                containsCodecParameters |= nalType is 32 or 33 or 34;
            }

            offset = nalOffset;
        }

        return new BitstreamInspection(isKeyFrame, containsCodecParameters);
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
