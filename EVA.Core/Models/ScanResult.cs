namespace EVA.Core.Models;

public sealed class ScanResult
{
    public List<FileEntry> ConfirmedChanges { get; } = new();
    public List<FileEntry> StableFiles { get; } = new();
    public List<FileEntry> UnstableFiles { get; } = new();
    public List<DirectoryEntry> EmptyDirectories { get; } = new();
    public List<string> UnreadableFiles { get; } = new();
}
