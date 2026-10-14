using System.Buffers.Binary;
using ImageMagick;
using KanbanContracts;

namespace KanbanApi.Application;

public sealed record ProcessedImage(byte[] Original, byte[] Thumbnail, string ContentType);
public sealed class ImageProcessor
{
    private static readonly SemaphoreSlim Decoders = new(2);
    static ImageProcessor()
    {
        ResourceLimits.Memory = 256 * 1024 * 1024;
        ResourceLimits.MaxMemoryRequest = 128 * 1024 * 1024;
        ResourceLimits.Disk = 0;
        ResourceLimits.Width = ImageLimits.MaxSide;
        ResourceLimits.Height = ImageLimits.MaxSide;
        ResourceLimits.MaxProfileSize = ImageLimits.MaxBytes;
        ResourceLimits.Thread = 2;
    }
    public async Task<ProcessedImage> Process(byte[] bytes, CancellationToken token)
    {
        if (bytes.Length == 0) throw new ApiException(400, "invalid_image", "Оберіть непорожнє зображення.");
        if (bytes.Length > ImageLimits.MaxBytes) throw new ApiException(413, "image_too_large", "Фото: не більше 5 МіБ.");
        var (format, mime) = Detect(bytes);
        await Decoders.WaitAsync(token);
        try
        {
            // Force the whitelisted decoder; never infer a delegate from a filename.
            var settings = new MagickReadSettings { Format = format };
            using var info = new MagickImage();
            var warning = false; info.Warning += (_, _) => warning = true;
            info.Ping(bytes, settings);
            if (info.Width == 0 || info.Height == 0 || info.Width > ImageLimits.MaxSide || info.Height > ImageLimits.MaxSide || (long)info.Width * info.Height > ImageLimits.MaxPixels)
                throw new ApiException(400, "image_dimensions", "Фото: до 20 мегапікселів і 10 000 пікселів по стороні.");
            token.ThrowIfCancellationRequested();
            using var image = new MagickImage(); image.Warning += (_, _) => warning = true;
            image.Read(bytes, settings);
            if (warning) throw Invalid();
            image.AutoOrient();
            if (image.Width > ImageLimits.ThumbnailSide || image.Height > ImageLimits.ThumbnailSide)
                image.Resize((uint)ImageLimits.ThumbnailSide, (uint)ImageLimits.ThumbnailSide);
            image.Strip(); var thumbnail = image.ToByteArray(MagickFormat.Png);
            token.ThrowIfCancellationRequested(); return new(bytes, thumbnail, mime);
        }
        catch (MagickException) { throw Invalid(); }
        finally { Decoders.Release(); }
    }
    private static (MagickFormat, string) Detect(byte[] bytes)
    {
        var b = bytes.AsSpan();
        if (b.Length >= 3 && b[0] == 0xff && b[1] == 0xd8 && b[2] == 0xff)
        { if (b.Length < 4 || b[^2] != 0xff || b[^1] != 0xd9) throw Invalid(); return (MagickFormat.Jpeg, "image/jpeg"); }
        if (b.StartsWith(new byte[] {137,80,78,71,13,10,26,10}))
        { if (b.Length < 20 || !b[^12..].SequenceEqual(new byte[] {0,0,0,0,73,69,78,68,174,66,96,130})) throw Invalid(); return (MagickFormat.Png, "image/png"); }
        if (b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b.Slice(8,4).SequenceEqual("WEBP"u8))
        { if ((long)BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(4,4)) + 8 != b.Length) throw Invalid(); return (MagickFormat.WebP, "image/webp"); }
        throw new ApiException(415, "unsupported_image", "Підтримуються JPG, PNG та WEBP за вмістом файлу.");
    }
    private static ApiException Invalid() => new(400, "invalid_image", "Фото пошкоджене або не може бути повністю декодоване.");
}
