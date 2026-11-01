using System.Buffers.Binary;

namespace Trofeo.Device.Tests;

public sealed class LyProtocolTests
{
    private static byte[] Jpeg(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
            data[i] = (byte)(i % 251 + 1); // jamais 0 : on distingue la charge du remplissage
        return data;
    }

    [Fact]
    public void Handshake_Bytes()
    {
        var handshake = LyProtocol.BuildHandshake();

        Assert.Equal(2048, handshake.Length);
        Assert.Equal(0x02, handshake[0]);
        Assert.Equal(0xFF, handshake[1]);
        Assert.Equal(0x01, handshake[8]);
        for (var i = 0; i < handshake.Length; i++)
        {
            if (i is 0 or 1 or 8)
                continue;
            Assert.Equal(0, handshake[i]);
        }
    }

    [Fact]
    public void HandshakeReply_Real()
    {
        var real = Convert.FromHexString("03ff0000000000000100000000000000498ed6bc02000400800700005702000041");

        Assert.True(LyProtocol.IsValidHandshakeReply(real));
        Assert.False(LyProtocol.IsValidHandshakeReply(new byte[] { 0x03, 0xFE, 0, 0, 0, 0, 0, 0, 0x01 }));
    }

    private static byte[] ReplyWithModelByte(byte model)
    {
        var reply = new byte[512];
        reply[0] = 0x03;
        reply[1] = 0xFF;
        reply[8] = 0x01;
        reply[20] = model;
        return reply;
    }

    [Theory]
    [InlineData(0, 65)]
    [InlineData(1, 65)]
    [InlineData(2, 65)]
    [InlineData(3, 65)]
    [InlineData(4, 68)]
    [InlineData(5, 69)]
    public void ReadModel_Is64PlusByte20_With0To3CountingForOne(byte byte20, int expected)
    {
        Assert.Equal(expected, LyProtocol.ReadModel(ReplyWithModelByte(byte20)));
    }

    [Fact]
    public void ReadModel_RealReply_IsTheSupportedModel()
    {
        var real = Convert.FromHexString("03ff0000000000000100000000000000498ed6bc02000400800700005702000041");

        Assert.Equal(65, LyProtocol.ReadModel(real));
        Assert.Equal(LyProtocol.SupportedModel, LyProtocol.ReadModel(real));
    }

    [Fact]
    public void IsValidHandshakeReply_TooShortToHoldTheModel_IsInvalid()
    {
        Assert.False(LyProtocol.IsValidHandshakeReply(ReplyWithModelByte(2).AsSpan(0, 20)));
    }

    [Fact]
    public void Packetize_1000Bytes()
    {
        var jpeg = Jpeg(1000);

        var buffer = LyProtocol.Packetize(jpeg);

        Assert.Equal(2048, buffer.Length);

        var p0 = buffer.AsSpan(0, 512);
        Assert.Equal(0x01, p0[0]);
        Assert.Equal(0xFF, p0[1]);
        Assert.Equal(1000u, BinaryPrimitives.ReadUInt32LittleEndian(p0[2..]));
        Assert.Equal(496, BinaryPrimitives.ReadUInt16LittleEndian(p0[6..]));
        Assert.Equal(0x01, p0[8]);
        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(p0[9..]));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(p0[11..]));
        Assert.True(p0[13..16].SequenceEqual(new byte[3]));
        Assert.True(p0[16..].SequenceEqual(jpeg.AsSpan(0, 496)));

        var p2 = buffer.AsSpan(1024, 512);
        Assert.Equal(8, BinaryPrimitives.ReadUInt16LittleEndian(p2[6..]));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(p2[11..]));
        Assert.True(p2.Slice(16, 8).SequenceEqual(jpeg.AsSpan(992, 8)));
        Assert.True(p2[24..].SequenceEqual(new byte[488]));

        Assert.True(buffer.AsSpan(1536, 512).SequenceEqual(new byte[512]));
    }

    [Fact]
    public void Packetize_992Bytes()
    {
        var buffer = LyProtocol.Packetize(Jpeg(992));

        Assert.Equal(2048, buffer.Length);
        var last = buffer.AsSpan(1024, 512);
        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(last[9..]));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(last[11..]));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(last[6..]));
        Assert.True(last[16..].SequenceEqual(new byte[496]));
    }

    [Fact]
    public void Packetize_MaxSize_Works()
    {
        var buffer = LyProtocol.Packetize(Jpeg(LyProtocol.MaxJpegSize));

        Assert.Equal(0, buffer.Length % 2048);
    }

    [Fact]
    public void Packetize_TooLarge_Throws()
    {
        Assert.Throws<ArgumentException>(() => LyProtocol.Packetize(new byte[450_001]));
    }
}
