using System.Buffers.Binary;

namespace Trofeo.BrandAssets.Tests;

public sealed class IcoWriterTests
{
    private static byte[] Fake(int length, byte fill) => Enumerable.Repeat(fill, length).ToArray();

    [Fact]
    public void Write_HeaderAndDirectory()
    {
        var png16 = Fake(40, 0x11);
        var png32 = Fake(70, 0x22);
        var png256 = Fake(300, 0x33);

        var ico = IcoWriter.Write([(16, png16), (32, png32), (256, png256)]);

        // En-tête : réservé 0, type 1 (icône), trois images.
        Assert.Equal(new byte[] { 0x00, 0x00, 0x01, 0x00 }, ico[..4]);
        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4, 2)));

        // Une entrée de 16 octets par image, juste après l'en-tête de 6 octets.
        var expectedOffset = 6 + 3 * 16;
        foreach (var (index, size, png) in new[] { (0, 16, png16), (1, 32, png32), (2, 256, png256) })
        {
            var entry = ico.AsSpan(6 + index * 16, 16);
            var written = size == 256 ? 0 : size; // 256 s'écrit 0
            Assert.Equal(written, entry[0]);
            Assert.Equal(written, entry[1]);
            Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(4, 2))); // plans
            Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(6, 2))); // bits par pixel
            Assert.Equal(png.Length, (int)BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(8, 4)));
            var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(12, 4));
            Assert.Equal(expectedOffset, offset);
            Assert.Equal(png, ico.AsSpan(offset, png.Length).ToArray());
            expectedOffset += png.Length;
        }

        Assert.Equal(expectedOffset, ico.Length);
    }

    [Fact]
    public void Write_NoImage_Throws()
    {
        Assert.Throws<ArgumentException>(() => IcoWriter.Write([]));
    }

    [Fact]
    public void Write_SizeOutOfRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => IcoWriter.Write([(257, Fake(4, 1))]));
    }
}
