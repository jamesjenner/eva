using EVA.App;
using EVA.Core;
using Xunit;

namespace EVA.Tests;

public sealed class ImagePreviewInspectionTests : IDisposable
{
    private readonly string _tempDirectory = CreateTempDirectory();

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("photo.png", true)]
    [InlineData("photo.PNG", true)]
    [InlineData("photo.jpg", true)]
    [InlineData("photo.JPEG", true)]
    [InlineData("photo.bmp", true)]
    [InlineData("photo.GIF", true)]
    [InlineData("photo", false)]
    [InlineData("photo.webp", false)]
    public void ExtensionDetectionIsCaseInsensitiveAndRestricted(string name, bool expected)
    {
        Assert.Equal(expected, ImagePreviewInspection.IsSupportedExtension(name));
    }

    [Fact]
    public void FileSizeCapRejectsOnlyFilesAboveLimit()
    {
        Assert.True(ImagePreviewInspection.IsWithinFileSizeLimit(ImagePreviewInspection.MaxFileSizeBytes));
        Assert.False(ImagePreviewInspection.IsWithinFileSizeLimit(ImagePreviewInspection.MaxFileSizeBytes + 1));
    }

    [Fact]
    public void PixelCapRejectsImagesAboveFiftyMegapixels()
    {
        Assert.True(ImagePreviewInspection.IsWithinPixelLimit(5000, 10000));
        Assert.False(ImagePreviewInspection.IsWithinPixelLimit(5001, 10000));
    }

    [Fact]
    public async Task CorruptImageBytesAreRejectedWithoutThrowing()
    {
        var path = Path.Combine(_tempDirectory, "corrupt.png");
        await File.WriteAllBytesAsync(path, [0x89, 0x50, 0x4E, 0x47, 0x00, 0x00]);

        var result = await ImagePreviewInspection.InspectFileAsync(path);

        Assert.False(result.IsSupported);
        Assert.NotEmpty(result.Reason);
    }

    [Fact]
    public void SelectionChangeDisposesAndClearsPreviousImageState()
    {
        var state = new PreviewSelectionState<FakePreviewResource>();
        var resource = new FakePreviewResource();
        state.SetImage(resource);
        state.SetFailure("previous failure");

        state.Clear();

        Assert.Null(state.Image);
        Assert.Null(state.Message);
        Assert.True(resource.IsDisposed);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eva-image-preview-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakePreviewResource : IDisposable
    {
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
    }
}