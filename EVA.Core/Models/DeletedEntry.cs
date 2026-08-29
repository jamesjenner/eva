namespace EVA.Core.Models;

public sealed class DeletedEntry
{
    public string RelativePath { get; set; } = string.Empty;
    public FileOperation Operation { get; set; } = FileOperation.Deleted;
    public DateTimeOffset DeletedAtUtc { get; set; }
}
