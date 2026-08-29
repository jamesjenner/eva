namespace EVA.Core.Models;

public sealed class ArchiveChain
{
    public string ChainId { get; set; } = string.Empty;
    public List<string> ArchiveIds { get; set; } = new();
    public ChainStatus Status { get; set; }
    public string? MissingArchiveId { get; set; }
}
