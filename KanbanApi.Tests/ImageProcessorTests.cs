using System.Buffers.Binary;
using System.IO.Compression;
using ImageMagick;
using KanbanApi.Application;
using Xunit;

namespace KanbanApi.Tests;

public sealed class ImageProcessorTests
{
    internal static byte[] ScreenshotLikePng()
    {
        using var image = new MagickImage(MagickColors.CornflowerBlue, 900, 600);
        image.Strip();
        var png = image.ToByteArray(MagickFormat.Png);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(ColorProfiles.SRGB.ToByteArray());
        // Reproduce the extra bytes after the compressed ICC stream in macOS PNGs.
        var payload = "ICC\0\0"u8.ToArray().Concat(compressed.ToArray()).Concat(new byte[8192]).ToArray();
        var chunk = new byte[payload.Length + 12];
        BinaryPrimitives.WriteInt32BigEndian(chunk, payload.Length);
        "iCCP"u8.CopyTo(chunk.AsSpan(4));
        payload.CopyTo(chunk.AsSpan(8));
        uint crc = uint.MaxValue;
        foreach (var value in chunk.AsSpan(4, payload.Length + 4))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
        }
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(chunk.Length - 4), ~crc);
        return png[..33].Concat(chunk).Concat(png[33..]).ToArray();
    }

    [Fact]
    public async Task PaddedMacOsColorProfileDoesNotRejectFullyDecodedPixels()
    {
        var png = ScreenshotLikePng();
        var warnings = new List<string>();
        using (var decoded = new MagickImage())
        {
            decoded.Warning += (_, e) => warnings.Add(e.Message);
            decoded.Read(png, new MagickReadSettings { Format = MagickFormat.Png });
        }
        Assert.Contains(warnings, w => w.StartsWith("iCCP: extra compressed data", StringComparison.Ordinal));
        var processed = await new ImageProcessor().Process(png, default);
        Assert.Equal(png, processed.Original);
        Assert.Equal("image/png", processed.ContentType);
        using var thumbnail = new MagickImage(processed.Thumbnail);
        Assert.Equal(480u, thumbnail.Width);
        Assert.Equal(320u, thumbnail.Height);
    }

    [Fact]
    public async Task PixelDataCrcFailureIsStillRejectedAlongsideHarmlessProfileWarning()
    {
        var error = await Assert.ThrowsAsync<ApiException>(() => new ImageProcessor().Process(WithBadPixelCrc(ScreenshotLikePng()), default));
        Assert.Equal("invalid_image", error.Code);
    }
    internal static byte[] WithBadPixelCrc(byte[] original)
    {
        var png = original.ToArray();
        for (var offset = 8; offset + 12 <= png.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            if (png.AsSpan(offset + 4, 4).SequenceEqual("IDAT"u8))
            { png[offset + 8 + length] ^= 1; break; }
            offset += length + 12;
        }
        return png;
    }
}
