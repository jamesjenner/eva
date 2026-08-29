namespace EVA.Core.Models;

public sealed class ArchiveManifest
{
    public ushort FormatVersion { get; set; }
    public string ArchiveId { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
    public ArchiveType ArchiveType { get; set; }
    public string ChainId { get; set; } = string.Empty;
    public string? ParentArchiveId { get; set; }
    public string SourceId { get; set; } = string.Empty;
    public List<FileEntry> Files { get; set; } = new();
    public List<DirectoryEntry> Directories { get; set; } = new();
    public List<DeletedEntry> Deleted { get; set; } = new();
}
