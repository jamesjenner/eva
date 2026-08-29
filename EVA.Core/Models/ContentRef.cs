namespace EVA.Core.Models;

public sealed class ContentRef
{
    public string Kind { get; set; } = string.Empty;
    public string ObjectId { get; set; } = string.Empty;
    public long Length { get; set; }
    public string? Compression { get; set; }
}
