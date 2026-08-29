namespace EVA.Core.Models;

public sealed class FileEntry
{
    public string RelativePath { get; set; } = string.Empty;
    public FileOperation Operation { get; set; }
    public long FileSize { get; set; }
    public DateTimeOffset LastModifiedUtc { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public ContentRef? ContentRef { get; set; }
}
