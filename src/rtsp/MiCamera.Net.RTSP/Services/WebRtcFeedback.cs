using System.Buffers.Binary;

namespace MiCamera.Net.RTSP.Services;

internal static class WebRtcFeedback
{
    // Called only after the library has authenticated and decrypted SRTCP. Its 10.0.17 report
    // parser retains only one PID/BLP pair, but a NACK can contain many pairs or compound reports.
    public static void Handle(ReadOnlySpan<byte> packet, uint mediaSsrc, Action<ushort> retransmit,
        Action requestKeyFrame, Action nackMessage)
    {
        while (packet.Length >= 4)
        {
            int length = (BinaryPrimitives.ReadUInt16BigEndian(packet[2..]) + 1) * 4;
            if (packet[0] >> 6 != 2 || length > packet.Length) return;
            ReadOnlySpan<byte> report = packet[..length];
            packet = packet[length..];
            if ((report[0] & 0x20) != 0)
            {
                int padding = report[^1];
                if (padding == 0 || padding > length - 4) return;
                report = report[..^padding];
            }
            if (report.Length < 12) continue;
            int format = report[0] & 31;
            uint target = BinaryPrimitives.ReadUInt32BigEndian(report[8..]);
            if (report[1] == 205 && format == 1 && target == mediaSsrc)
            {
                if ((report.Length - 12) % 4 != 0) return;
                nackMessage();
                for (int offset = 12; offset + 4 <= report.Length; offset += 4)
                {
                    ushort pid = BinaryPrimitives.ReadUInt16BigEndian(report[offset..]);
                    ushort mask = BinaryPrimitives.ReadUInt16BigEndian(report[(offset + 2)..]);
                    retransmit(pid);
                    for (int bit = 0; bit < 16; bit++)
                        if ((mask & (1 << bit)) != 0) retransmit(unchecked((ushort)(pid + bit + 1)));
                }
            }
            else if (report[1] == 206 && format == 1 && target == mediaSsrc)
            {
                requestKeyFrame();
            }
            else if (report[1] == 206 && format == 4)
            {
                // FIR addresses the encoder in each FCI entry; its header MediaSSRC is zero.
                for (int offset = 12; offset + 8 <= report.Length; offset += 8)
                    if (BinaryPrimitives.ReadUInt32BigEndian(report[offset..]) == mediaSsrc)
                    {
                        requestKeyFrame();
                        break;
                    }
            }
        }
    }
}
