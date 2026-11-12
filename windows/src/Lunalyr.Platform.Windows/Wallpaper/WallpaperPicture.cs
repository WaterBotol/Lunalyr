// Decodes an imported wallpaper picture for drawing: Windows' own image decoders (the WinRT BitmapDecoder over WIC), so
// whatever codecs this PC has work, HEIC and AVIF included where their extensions are installed. EXIF orientation is
// applied (the picture comes out upright) and colours are managed to sRGB. A file that can't be read gives null, never
// an exception. The Mac's CustomWallpaperView.decode (Lunalyr's own lines) is the behaviour ported.

using Lunalyr.Core.Colour;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Lunalyr.Platform.Wallpaper;

public static class WallpaperPicture
{
    /// <summary>
    /// The longest side a wallpaper picture is decoded at. The Mac's is 8192; Windows keeps 4096 (a 4K display's width),
    /// which needs a quarter of the memory and shows no difference on screen.
    /// </summary>
    public const int MaxSide = 4096;

    /// <summary>
    /// Decodes <paramref name="path"/> off the caller's thread: upright, scaled (never enlarged) so its longer side is at
    /// most <paramref name="maxSide"/>, as premultiplied BGRA. Null when it can't be read.
    /// </summary>
    public static Task<CoverArt?> DecodeAsync(string path, int maxSide = MaxSide, CancellationToken cancellationToken = default) =>
        Task.Run(() => DecodeCore(path, maxSide, cancellationToken), cancellationToken);

    private static async Task<CoverArt?> DecodeCore(string path, int maxSide, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using IRandomAccessStream stream = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            cancellationToken.ThrowIfCancellationRequested();
            uint width = decoder.PixelWidth, height = decoder.PixelHeight;
            if (width == 0 || height == 0 || width > 65536 || height > 65536) return null;
            // The transform scales the frame as stored; the orientation turns it afterwards.
            var scale = Math.Min(1.0, maxSide / (double)Math.Max(width, height));
            uint scaledWidth = Math.Max(1, (uint)Math.Round(width * scale)), scaledHeight = Math.Max(1, (uint)Math.Round(height * scale));
            var transform = new BitmapTransform
            {
                ScaledWidth = scaledWidth, ScaledHeight = scaledHeight, InterpolationMode = BitmapInterpolationMode.Fant,
            };
            var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
            var pixels = data.DetachPixelData();
            // Turned a quarter: the stored width becomes the height.
            var turned = decoder.OrientedPixelWidth != decoder.PixelWidth;
            int outWidth = (int)(turned ? scaledHeight : scaledWidth), outHeight = (int)(turned ? scaledWidth : scaledHeight);
            if (pixels.Length != outWidth * outHeight * 4) return null;
            return new CoverArt(outWidth, outHeight, pixels);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Not a picture, a codec this PC lacks (HEIC or AVIF without their extensions), or a damaged file.
            return null;
        }
    }

    /// <summary>File types offered as pictures (the decoders built into Windows, plus HEIC and AVIF where installed).</summary>
    public static IReadOnlyList<string> Extensions { get; } =
        [".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif", ".avif", ".jxr", ".wdp", ".ico", ".dds"];

    /// <summary>File types offered as videos (what Media Foundation plays on Windows 11).</summary>
    public static IReadOnlyList<string> VideoExtensions { get; } =
        [".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".3gp", ".mpg", ".mpeg", ".ts", ".m2ts"];

    /// <summary>Whether <paramref name="path"/> is a video, by its extension (the Mac's <c>isVideo</c>).</summary>
    public static bool IsVideo(string path) =>
        VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="path"/> is a picture, by its extension.</summary>
    public static bool IsPicture(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
