namespace EVA.Core.Models;

public sealed record PreviewCacheResult(
    string ArchiveId,
    string? CacheDirectory,
    IReadOnlyList<string> SkippedEntries,
    bool WasAlreadyCached,
    bool IsTooLarge);