namespace EVA.Core.Models;

public sealed class DirectoryEntry
{
    public string RelativePath { get; set; } = string.Empty;
    public FileOperation Operation { get; set; }
    public bool Exists { get; set; }
    public DateTimeOffset? LastModifiedUtc { get; set; }
}
