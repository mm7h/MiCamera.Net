using MiCamera.Net.Abstractions.Common.Enums;
using MiCamera.Net.Media.Services;
using MiCamera.Net.RTSP.Abstractions.Media;

namespace MiCamera.Net.RTSP.Server;

internal static class RtpPacketizer
{
    public static IReadOnlyList<byte[]> Packetize(VideoAccessUnit unit, uint ssrc, ref ushort sequenceNumber, int mtu)
    {
        int maxPayload = mtu - 12;
        if (maxPayload < 256)
        {
            throw new ArgumentOutOfRangeException(nameof(mtu), "RTP MTU 必须能够容纳至少 256 字节的有效载荷。");
        }

        IReadOnlyList<ReadOnlyMemory<byte>> nals = AnnexBBitstream.SplitNalUnits(unit.AnnexB);
        List<byte[]> packets = [];

        for (int index = 0; index < nals.Count; index++)
        {
            ReadOnlySpan<byte> nal = nals[index].Span;
            if (nal.IsEmpty)
            {
                continue;
            }

            bool finalNal = index == nals.Count - 1;
            if (unit.Codec == VideoCodec.H264)
            {
                PacketizeH264(nal, unit.Timestamp90Khz, ssrc, ref sequenceNumber, maxPayload, finalNal, packets);
            }
            else
            {
                PacketizeH265(nal, unit.Timestamp90Khz, ssrc, ref sequenceNumber, maxPayload, finalNal, packets);
            }
        }

        return packets;
    }

    private static void PacketizeH264(
        ReadOnlySpan<byte> nal,
        uint timestamp,
        uint ssrc,
        ref ushort sequenceNumber,
        int maxPayload,
        bool finalNal,
        ICollection<byte[]> packets)
    {
        if (nal.Length <= maxPayload)
        {
            packets.Add(CreatePacket(nal, timestamp, ssrc, ref sequenceNumber, finalNal));
            return;
        }

        int fragmentCapacity = maxPayload - 2;
        byte indicator = (byte)((nal[0] & 0xe0) | 28);
        byte nalType = (byte)(nal[0] & 0x1f);
        int offset = 1;

        while (offset < nal.Length)
        {
            int count = Math.Min(fragmentCapacity, nal.Length - offset);
            bool start = offset == 1;
            bool end = offset + count == nal.Length;
            byte[] payload = new byte[count + 2];
            payload[0] = indicator;
            payload[1] = (byte)((start ? 0x80 : 0) | (end ? 0x40 : 0) | nalType);
            nal.Slice(offset, count).CopyTo(payload.AsSpan(2));
            packets.Add(CreatePacket(payload, timestamp, ssrc, ref sequenceNumber, finalNal && end));
            offset += count;
        }
    }

    private static void PacketizeH265(
        ReadOnlySpan<byte> nal,
        uint timestamp,
        uint ssrc,
        ref ushort sequenceNumber,
        int maxPayload,
        bool finalNal,
        ICollection<byte[]> packets)
    {
        if (nal.Length < 3)
        {
            return;
        }

        if (nal.Length <= maxPayload)
        {
            packets.Add(CreatePacket(nal, timestamp, ssrc, ref sequenceNumber, finalNal));
            return;
        }

        int fragmentCapacity = maxPayload - 3;
        int nalType = (nal[0] >> 1) & 0x3f;
        byte indicator0 = (byte)((nal[0] & 0x81) | (49 << 1));
        byte indicator1 = nal[1];
        int offset = 2;

        while (offset < nal.Length)
        {
            int count = Math.Min(fragmentCapacity, nal.Length - offset);
            bool start = offset == 2;
            bool end = offset + count == nal.Length;
            byte[] payload = new byte[count + 3];
            payload[0] = indicator0;
            payload[1] = indicator1;
            payload[2] = (byte)((start ? 0x80 : 0) | (end ? 0x40 : 0) | nalType);
            nal.Slice(offset, count).CopyTo(payload.AsSpan(3));
            packets.Add(CreatePacket(payload, timestamp, ssrc, ref sequenceNumber, finalNal && end));
            offset += count;
        }
    }

    private static byte[] CreatePacket(
        ReadOnlySpan<byte> payload,
        uint timestamp,
        uint ssrc,
        ref ushort sequenceNumber,
        bool marker)
    {
        byte[] packet = new byte[payload.Length + 12];
        packet[0] = 0x80;
        packet[1] = (byte)(96 | (marker ? 0x80 : 0));
        packet[2] = (byte)(sequenceNumber >> 8);
        packet[3] = (byte)sequenceNumber;
        packet[4] = (byte)(timestamp >> 24);
        packet[5] = (byte)(timestamp >> 16);
        packet[6] = (byte)(timestamp >> 8);
        packet[7] = (byte)timestamp;
        packet[8] = (byte)(ssrc >> 24);
        packet[9] = (byte)(ssrc >> 16);
        packet[10] = (byte)(ssrc >> 8);
        packet[11] = (byte)ssrc;
        payload.CopyTo(packet.AsSpan(12));
        sequenceNumber++;
        return packet;
    }
}
