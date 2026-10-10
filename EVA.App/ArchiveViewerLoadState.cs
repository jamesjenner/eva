using EVA.Infrastructure;

namespace EVA.App;

public enum ArchiveContentsLoadStatus
{
    Loading,
    Loaded,
    Failed
}

public sealed class ArchiveViewerLoadState
{
    public ArchiveContentsLoadStatus ContentsStatus { get; private set; } = ArchiveContentsLoadStatus.Loaded;
    public string ContentsMessage { get; private set; } = "Select an archive";
    public string PreviewMessage { get; private set; } = "Select a file to preview";

    public void BeginArchiveSelection()
    {
        ContentsStatus = ArchiveContentsLoadStatus.Loading;
        ContentsMessage = "Loading archive contents...";
        PreviewMessage = "Select a file to preview";
    }

    public void MarkArchiveLoaded()
    {
        ContentsStatus = ArchiveContentsLoadStatus.Loaded;
        ContentsMessage = string.Empty;
    }

    public void MarkArchiveFailed(string message)
    {
        ContentsStatus = ArchiveContentsLoadStatus.Failed;
        ContentsMessage = message;
    }

    public void SetPreviewMessage(string message) => PreviewMessage = message;
}

public sealed class ArchiveTooLargeException : IOException
{
    public ArchiveTooLargeException() : base("Archive too large to open") { }
}

public static class ArchiveManifestLoader
{
    public static Task<EVA.Core.Models.ArchiveManifest> LoadAsync(
        string archivePath,
        string password,
        EVA.Core.Interfaces.IArchiveReader archiveReader,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        ArgumentNullException.ThrowIfNull(archiveReader);
        cancellationToken.ThrowIfCancellationRequested();

        var file = new FileInfo(archivePath);
        if (file.Exists && file.Length > PreviewCacheService.MaxArchiveSizeBytes)
        {
            throw new ArchiveTooLargeException();
        }

        return archiveReader.ReadManifestAsync(archivePath, password, cancellationToken);
    }
}

public static class ArchiveLoadErrorMessages
{
    public static string ForException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is ArchiveTooLargeException)
        {
            return "Archive too large to open";
        }

        if (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return "Archive file not found.";
        }

        if (exception is UnauthorizedAccessException)
        {
            return "Permission denied while reading the archive.";
        }

        if (exception.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase))
        {
            return "The password is wrong or the archive has been modified.";
        }

        if (exception is InvalidDataException ||
            exception.Message.Contains("truncated", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("length is inconsistent", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("too small", StringComparison.OrdinalIgnoreCase))
        {
            return "The archive is truncated or invalid.";
        }

        if (exception is IOException)
        {
            return "The archive could not be read because of an I/O error.";
        }

        return "Could not open the archive.";
    }
}