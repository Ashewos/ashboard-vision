using System.Buffers.Binary;
using SkiaSharp;

namespace Trofeo.BrandAssets.Tests;

public sealed class BmpWriterTests
{
    [Fact]
    public void Write_24Bits_BottomUp_BgrAndPaddedRows()
    {
        // 3 pixels de large : 9 octets par ligne, complétés à 12.
        using var bitmap = new SKBitmap(3, 2);
        bitmap.Erase(SKColors.Black);
        bitmap.SetPixel(0, 0, new SKColor(0x11, 0x22, 0x33)); // haut gauche
        bitmap.SetPixel(2, 1, new SKColor(0xAA, 0xBB, 0xCC)); // bas droite

        var bmp = BmpWriter.Write(bitmap, SKColors.White);

        Assert.Equal((byte)'B', bmp[0]);
        Assert.Equal((byte)'M', bmp[1]);
        Assert.Equal(54 + 12 * 2, bmp.Length);
        Assert.Equal(bmp.Length, (int)BinaryPrimitives.ReadUInt32LittleEndian(bmp.AsSpan(2, 4)));
        Assert.Equal(54, (int)BinaryPrimitives.ReadUInt32LittleEndian(bmp.AsSpan(10, 4)));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(18, 4)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(22, 4))); // positif : de bas en haut
        Assert.Equal(24, BinaryPrimitives.ReadUInt16LittleEndian(bmp.AsSpan(28, 2)));
        Assert.Equal(0, (int)BinaryPrimitives.ReadUInt32LittleEndian(bmp.AsSpan(30, 4))); // BI_RGB

        // Première ligne écrite = ligne du bas : son dernier pixel est AA BB CC, écrit B, G, R.
        Assert.Equal(new byte[] { 0xCC, 0xBB, 0xAA }, bmp[(54 + 6)..(54 + 9)]);
        // Seconde ligne écrite = ligne du haut : son premier pixel est 11 22 33.
        Assert.Equal(new byte[] { 0x33, 0x22, 0x11 }, bmp[(54 + 12)..(54 + 15)]);
    }

    [Fact]
    public void Write_AlphaIsFlattenedOnTheBackground()
    {
        using var bitmap = new SKBitmap(1, 1);
        bitmap.Erase(SKColors.Transparent);

        var bmp = BmpWriter.Write(bitmap, new SKColor(0x20, 0x21, 0x22));

        Assert.Equal(new byte[] { 0x22, 0x21, 0x20 }, bmp[54..57]);
    }
}
