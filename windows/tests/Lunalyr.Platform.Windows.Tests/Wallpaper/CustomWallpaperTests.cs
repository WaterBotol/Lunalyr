using Lunalyr.Platform.Media;
using Lunalyr.Platform.Wallpaper;

namespace Lunalyr.Platform.Windows.Tests.Wallpaper;

/// <summary>
/// "Imported image or video": the copy Lunalyr keeps (ported from the Mac's CustomWallpaperTests), decoding pictures
/// upright, and reading a video silently. Pictures are written here with Windows' own encoder; the clip and the
/// turned JPEG are fixtures made by tools/WallpaperTestMedia.
/// </summary>
public class CustomWallpaperTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Wallpaper", name);

    /// <summary>A genuine PNG from Windows' own encoder (so a valid image really is valid), blue.</summary>
    private static byte[] PngData(int width = 2, int height = 2) => EncodePng(width, height).GetAwaiter().GetResult();

    private static async Task<byte[]> EncodePng(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = ((byte)204, (byte)102, (byte)51, (byte)255);
        using var stream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await global::Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(global::Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, global::Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
            (uint)width, (uint)height, 96, 96, pixels);
        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        using var reader = new global::Windows.Storage.Streams.DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    // Ported from CustomWallpaperTests.swift: testImportedImageHasIndependentDurableCopy
    [Fact]
    public async Task ImportedImageHasIndependentDurableCopy()
    {
        using var root = new TempFolder();
        var source = root.File("original.png");
        var pixel = PngData();
        await File.WriteAllBytesAsync(source, pixel, TestContext.Current.CancellationToken);
        var imported = await CustomWallpaperStore.ImportAsync(source, Path.Combine(root.Path, "library"), TestContext.Current.CancellationToken);
        Assert.NotEqual(source, imported.Path);
        Assert.Equal("original.png", imported.OriginalName);
        Assert.Equal(pixel, await File.ReadAllBytesAsync(source, TestContext.Current.CancellationToken));
        File.Delete(source);
        Assert.Equal(pixel, await File.ReadAllBytesAsync(imported.Path, TestContext.Current.CancellationToken));
        Assert.False(CustomWallpaperStore.IsVideo(imported.Path));
    }

    // Ported from CustomWallpaperTests.swift: testInvalidImageIsRejectedWithoutSelectingOrCopyingIt
    [Fact]
    public async Task InvalidImageIsRejectedWithoutSelectingOrCopyingIt()
    {
        using var root = new TempFolder();
        var source = root.File("invalid.png");
        var library = Path.Combine(root.Path, "library");
        await File.WriteAllTextAsync(source, "not an image", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<WallpaperImportException>(() => CustomWallpaperStore.ImportAsync(source, library, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(library));
    }

    // Ported from CustomWallpaperTests.swift: testDefaultLibraryStaysInsideTheTestHome. Windows keeps imported copies in
    // the data folder (the Mac in Application Support), so a test's data folder keeps its own.
    [Fact]
    public async Task DefaultLibraryStaysInsideTheDataFolder()
    {
        using var data = new TempFolder();
        using var elsewhere = new TempFolder();
        var source = elsewhere.File("default.png");
        var pixel = PngData();
        await File.WriteAllBytesAsync(source, pixel, TestContext.Current.CancellationToken);
        var imported = await CustomWallpaperStore.ImportAsync(source, CustomWallpaperStore.DefaultFolder(data.Path), TestContext.Current.CancellationToken);
        Assert.StartsWith(data.Path, imported.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(data.Path, "Wallpapers"), Path.GetDirectoryName(imported.Path), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(pixel, await File.ReadAllBytesAsync(imported.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AFileThatIsNeitherIsRefusedWithTheMacsWords()
    {
        using var root = new TempFolder();
        var source = root.File("notes.txt");
        await File.WriteAllTextAsync(source, "hello", TestContext.Current.CancellationToken);
        var refused = await Assert.ThrowsAsync<WallpaperImportException>(() => CustomWallpaperStore.ImportAsync(source, root.Path, TestContext.Current.CancellationToken));
        Assert.Equal("Choose an image or a video file.", refused.Message);
        var video = root.File("broken.mp4");
        await File.WriteAllTextAsync(video, "not a video", TestContext.Current.CancellationToken);
        refused = await Assert.ThrowsAsync<WallpaperImportException>(() => CustomWallpaperStore.ImportAsync(video, Path.Combine(root.Path, "library"), TestContext.Current.CancellationToken));
        Assert.Equal("This file has no playable video track.", refused.Message);
    }

    [Fact]
    public async Task OnlyTheReplacedCopyInLunalyrsOwnFolderIsDeleted()
    {
        using var root = new TempFolder();
        var library = Path.Combine(root.Path, "Wallpapers");
        Directory.CreateDirectory(library);
        var old = Path.Combine(library, "old.png");
        var current = Path.Combine(library, "new.png");
        var outside = root.File("outside.png");
        foreach (var file in new[] { old, current, outside }) await File.WriteAllBytesAsync(file, PngData(), TestContext.Current.CancellationToken);
        Assert.False(CustomWallpaperStore.DiscardReplaced(outside, current, library));
        Assert.False(CustomWallpaperStore.DiscardReplaced(current, current, library));
        Assert.False(CustomWallpaperStore.DiscardReplaced("", current, library));
        Assert.True(CustomWallpaperStore.DiscardReplaced(old, current, library));
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(current) && File.Exists(outside));
        Assert.True(CustomWallpaperStore.IsAvailable(current));
        Assert.False(CustomWallpaperStore.IsAvailable(old));
        Assert.False(CustomWallpaperStore.IsAvailable(library)); // a folder is not a file
    }

    // Ported from WallpaperSurfaceMotionTests.swift: testAnImportedPictureDecodesUprightOffTheMainThread
    [Fact]
    public async Task AnImportedPictureDecodesUprightOffTheMainThread()
    {
        using var root = new TempFolder();
        var path = root.File("wallpaper.png");
        await File.WriteAllBytesAsync(path, PngData(40, 20), TestContext.Current.CancellationToken);
        var image = await WallpaperPicture.DecodeAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(image);
        Assert.Equal((40, 20), (image.Width, image.Height));

        var garbage = root.File("broken.png");
        await File.WriteAllTextAsync(garbage, "not a picture", TestContext.Current.CancellationToken);
        Assert.Null(await WallpaperPicture.DecodeAsync(garbage, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task APictureTurnedByItsExifOrientationComesOutUprightAndScaled()
    {
        // Stored 1600×900 sideways (red left, blue right) with orientation 6: upright it is 900×1600, red over blue.
        var image = await WallpaperPicture.DecodeAsync(Fixture("turned.jpg"), maxSide: 400, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(image);
        Assert.Equal((225, 400), (image.Width, image.Height));
        byte Red(int x, int y) => image.Pixels[(y * image.Width + x) * 4 + 2];
        Assert.True(Red(112, 50) > 180, "the top should be red");
        Assert.True(Red(112, 350) < 80, "the bottom should be blue");
    }

    [Fact]
    public void AVideoIsReadSilentlyFrameByFrameAndLoops()
    {
        // Software decoding (no device): the same reader the wallpaper uses, which never selects an audio stream.
        using var reader = VideoReader.Open(Fixture("clip.mp4"), 0, out var error);
        Assert.True(reader is not null, error);
        Assert.Equal((160, 96), (reader.Width, reader.Height));
        Assert.Equal(1, reader.Duration, 1);
        var frames = 0;
        double last = -1;
        while (true)
        {
            var read = reader.Read(out var frame);
            using (frame)
            {
                if (read == VideoRead.EndOfStream) break;
                Assert.NotEqual(VideoRead.Failed, read);
                if (read != VideoRead.Frame) continue;
                Assert.True(frame.Pitch >= 160 * 4); // a row of BGRA in memory
                Assert.True(frame.Time > last);
                last = frame.Time;
                frames++;
            }
        }
        Assert.InRange(frames, 14, 16);
        Assert.True(reader.Rewind());
        Assert.Equal(VideoRead.Frame, reader.Read(out var again));
        using (again) Assert.True(again.Time < 0.1);
        Assert.True(VideoReader.HasVideo(Fixture("clip.mp4"), out _));
    }
}
