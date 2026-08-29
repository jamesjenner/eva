namespace EVA.Core.Models;

public sealed class ArchiveHeader
{
    public string Magic { get; set; } = string.Empty;
    public ushort FormatVersion { get; set; }
    public ArchiveType ArchiveType { get; set; }
    public string ArchiveId { get; set; } = string.Empty;
    public string ChainId { get; set; } = string.Empty;
    public string? ParentArchiveId { get; set; }
    public string SourceId { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
    public string KdfIdentifier { get; set; } = string.Empty;
    public byte[] KdfParameters { get; set; } = Array.Empty<byte>();
    public byte[] Salt { get; set; } = Array.Empty<byte>();
    public string EncryptionAlgorithmIdentifier { get; set; } = string.Empty;
    public byte[] Nonce { get; set; } = Array.Empty<byte>();
    public long HeaderLength { get; set; }
    public long PayloadLength { get; set; }
}
