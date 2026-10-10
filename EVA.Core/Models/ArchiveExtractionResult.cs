namespace EVA.Core.Models;

public sealed record ArchiveExtractionResult(string ArchiveId, IReadOnlyList<string> SkippedEntries);