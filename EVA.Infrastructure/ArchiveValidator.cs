using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EVA.Core.Interfaces;
using EVA.Core.Models;

namespace EVA.Infrastructure;

public sealed class ArchiveValidator : IArchiveValidator
{
    public bool ValidateHeader(ArchiveHeader header)
    {
        if (header is null)
        {
            return false;
        }

        if (!string.Equals(header.Magic, "EVA1", StringComparison.Ordinal))
        {
            return false;
        }

        if (header.FormatVersion != 1)
        {
            return false;
        }

        if (header.HeaderLength <= 0 || header.KdfParameters.Length != 16)
        {
            return false;
        }

        if (header.Salt.Length == 0 || header.Nonce.Length == 0)
        {
            return false;
        }

        return true;
    }

    public bool ValidateManifest(ArchiveManifest manifest, ArchiveHeader header)
    {
        if (manifest is null || header is null)
        {
            return false;
        }

        if (!ValidateHeader(header))
        {
            return false;
        }

        if (manifest.FormatVersion != header.FormatVersion)
        {
            return false;
        }

        if (manifest.ArchiveId != header.ArchiveId)
        {
            return false;
        }

        if (manifest.ChainId != header.ChainId)
        {
            return false;
        }

        if (manifest.SourceId != header.SourceId)
        {
            return false;
        }

        if ((manifest.ParentArchiveId ?? string.Empty) != (header.ParentArchiveId ?? string.Empty))
        {
            return false;
        }

        if (manifest.CreatedUtc != header.CreatedUtc)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(manifest.ArchiveId))
        {
            return false;
        }

        if (manifest.Files.Any(f => string.IsNullOrWhiteSpace(f.RelativePath) || string.IsNullOrWhiteSpace(f.Sha256)))
        {
            return false;
        }

        return true;
    }

    public bool ValidateArchiveChain(IReadOnlyCollection<ArchiveHeader> headers, ArchiveChain chain)
    {
        if (headers is null || chain is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(chain.ChainId))
        {
            return false;
        }

        var archiveHeaders = headers.ToList();
        if (archiveHeaders.Count == 0)
        {
            return false;
        }

        var completeMap = archiveHeaders.ToDictionary(h => h.ArchiveId, StringComparer.OrdinalIgnoreCase);
        var expectedChain = chain.ArchiveIds;

        foreach (var archiveId in expectedChain)
        {
            if (!completeMap.ContainsKey(archiveId))
            {
                return false;
            }
        }

        return chain.Status == ChainStatus.Complete;
    }

    public static ArchiveHeader? ReadHeaderFromPath(string archivePath)
    {
        if (!File.Exists(archivePath))
        {
            return null;
        }

        var bytes = File.ReadAllBytes(archivePath);
        if (bytes.Length < 46)
        {
            return null;
        }

        try
        {
            var header = new ArchiveHeader
            {
                Magic = Encoding.ASCII.GetString(bytes.AsSpan(0, 4)),
                FormatVersion = BitConverter.ToUInt16(bytes, 4),
                ArchiveType = (ArchiveType)bytes[6],
                ArchiveId = Encoding.UTF8.GetString(bytes, 46, BitConverter.ToUInt16(bytes, 20)),
                ChainId = Encoding.UTF8.GetString(bytes, 46 + BitConverter.ToUInt16(bytes, 20), BitConverter.ToUInt16(bytes, 22)),
                ParentArchiveId = string.IsNullOrEmpty(Encoding.UTF8.GetString(bytes, 46 + BitConverter.ToUInt16(bytes, 20) + BitConverter.ToUInt16(bytes, 22), BitConverter.ToUInt16(bytes, 24))) ? null : Encoding.UTF8.GetString(bytes, 46 + BitConverter.ToUInt16(bytes, 20) + BitConverter.ToUInt16(bytes, 22), BitConverter.ToUInt16(bytes, 24)),
                SourceId = Encoding.UTF8.GetString(bytes, 46 + BitConverter.ToUInt16(bytes, 20) + BitConverter.ToUInt16(bytes, 22) + BitConverter.ToUInt16(bytes, 24), BitConverter.ToUInt16(bytes, 26)),
                CreatedUtc = DateTimeOffset.FromUnixTimeMilliseconds(BitConverter.ToInt64(bytes, 28)),
                KdfIdentifier = Encoding.UTF8.GetString(bytes, 46 + BitConverter.ToUInt16(bytes, 20) + BitConverter.ToUInt16(bytes, 22) + BitConverter.ToUInt16(bytes, 24) + BitConverter.ToUInt16(bytes, 26), BitConverter.ToUInt16(bytes, 36)),
                KdfParameters = new byte[16],
                Salt = Array.Empty<byte>(),
                EncryptionAlgorithmIdentifier = Encoding.UTF8.GetString(bytes, 46 + BitConverter.ToUInt16(bytes, 20) + BitConverter.ToUInt16(bytes, 22) + BitConverter.ToUInt16(bytes, 24) + BitConverter.ToUInt16(bytes, 26) + BitConverter.ToUInt16(bytes, 36) + BitConverter.ToUInt16(bytes, 38) + BitConverter.ToUInt16(bytes, 40) + BitConverter.ToUInt16(bytes, 42), BitConverter.ToUInt16(bytes, 42)),
                Nonce = Array.Empty<byte>(),
                HeaderLength = BitConverter.ToUInt32(bytes, 8),
                PayloadLength = BitConverter.ToInt64(bytes, 12)
            };

            return header;
        }
        catch
        {
            return null;
        }
    }
}
