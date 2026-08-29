using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EVA.Core.Interfaces;
using EVA.Core.Models;
using Konscious.Security.Cryptography;

namespace EVA.Infrastructure;

public sealed class ArchiveReader : IArchiveReader
{
    private const string Magic = "EVA1";
    private const ushort FormatVersion = 1;
    private const int AuthenticationTagLength = 16;
    private const int MinimumHeaderLength = 46;
    private const int MinimumArchiveLength = MinimumHeaderLength + AuthenticationTagLength;

    public Task<ArchiveHeader> ReadHeaderAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            throw new ArgumentException("Archive path is required.", nameof(archivePath));
        }

        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("Archive file was not found.", archivePath);
        }

        var bytes = File.ReadAllBytes(archivePath);
        if (bytes.Length < MinimumArchiveLength)
        {
            throw new InvalidOperationException("Archive is too small to contain a valid header and authentication tag.");
        }

        var header = ParseHeader(bytes);
        ValidateHeaderConsistency(header, bytes.Length);
        return Task.FromResult(header);
    }

    public async Task<ArchiveManifest> ReadManifestAsync(string archivePath, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            throw new ArgumentException("Archive path is required.", nameof(archivePath));
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("Password is required.", nameof(password));
        }

        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("Archive file was not found.", archivePath);
        }

        var bytes = await File.ReadAllBytesAsync(archivePath, cancellationToken).ConfigureAwait(false);
        if (bytes.Length < MinimumArchiveLength)
        {
            throw new InvalidOperationException("Archive is truncated before the header and tag are complete.");
        }

        var header = ParseHeader(bytes);
        ValidateHeaderConsistency(header, bytes.Length);

        if (!string.Equals(header.Magic, Magic, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Archive magic bytes are invalid.");
        }

        if (header.FormatVersion != FormatVersion)
        {
            throw new InvalidOperationException("Archive format version is unsupported.");
        }

        var aad = bytes.AsSpan(0, (int)header.HeaderLength).ToArray();
        var ciphertextLength = (int)header.PayloadLength;
        var minimumLength = (int)header.HeaderLength + ciphertextLength + AuthenticationTagLength;
        if (bytes.Length < minimumLength)
        {
            throw new InvalidOperationException("Archive length is inconsistent with the public header.");
        }

        var ciphertext = bytes.AsSpan((int)header.HeaderLength, ciphertextLength).ToArray();
        var tag = bytes.AsSpan(bytes.Length - AuthenticationTagLength, AuthenticationTagLength).ToArray();

        var key = DeriveKey(password, header.Salt, header.KdfParameters);
        var plaintext = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(key, AuthenticationTagLength);
            aes.Decrypt(header.Nonce, ciphertext, tag, plaintext, aad);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("AES-GCM authentication failed.", ex);
        }

        using var compressedStream = new MemoryStream(plaintext);
        using var decompressor = new GZipStream(compressedStream, CompressionMode.Decompress);
        using var output = new MemoryStream();
        await decompressor.CopyToAsync(output, cancellationToken).ConfigureAwait(false);

        var json = Encoding.UTF8.GetString(output.ToArray());
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("manifest", out var manifestElement))
        {
            throw new InvalidOperationException("Archive payload does not contain a manifest.");
        }

        var manifest = manifestElement.Deserialize<ArchiveManifest>(new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Archive manifest is missing.");

        ValidateManifestIdentity(manifest, header);
        ValidateManifestStructure(manifest);

        return manifest;
    }

    public Task<bool> ValidateArchiveAsync(string archivePath, string password, CancellationToken cancellationToken = default)
    {
        try
        {
            _ = ReadManifestAsync(archivePath, password, cancellationToken).GetAwaiter().GetResult();
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    private static void ValidateHeaderConsistency(ArchiveHeader header, long totalFileLength)
    {
        if (!string.Equals(header.Magic, Magic, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Archive magic bytes are invalid.");
        }

        if (header.FormatVersion != FormatVersion)
        {
            throw new InvalidOperationException("Archive format version is unsupported.");
        }

        if (header.HeaderLength <= 0 || header.HeaderLength < MinimumHeaderLength)
        {
            throw new InvalidOperationException("Archive header length is invalid.");
        }

        if (header.HeaderLength + AuthenticationTagLength > totalFileLength)
        {
            throw new InvalidOperationException("Archive is truncated or incomplete.");
        }

        if (header.KdfParameters.Length != 16)
        {
            throw new InvalidOperationException("Archive KDF parameters are malformed.");
        }

        if (header.Salt.Length == 0 || header.Nonce.Length == 0)
        {
            throw new InvalidOperationException("Archive salt or nonce is missing.");
        }

        if (header.PayloadLength < 0 || header.PayloadLength > totalFileLength)
        {
            throw new InvalidOperationException("Archive payload length is inconsistent with the file size.");
        }
    }

    private static void ValidateManifestIdentity(ArchiveManifest manifest, ArchiveHeader header)
    {
        if (manifest.ArchiveId != header.ArchiveId)
        {
            throw new InvalidOperationException("Archive manifest archiveId does not match the header.");
        }

        if (manifest.ChainId != header.ChainId)
        {
            throw new InvalidOperationException("Archive manifest chainId does not match the header.");
        }

        if (manifest.SourceId != header.SourceId)
        {
            throw new InvalidOperationException("Archive manifest sourceId does not match the header.");
        }

        if ((manifest.ParentArchiveId ?? string.Empty) != (header.ParentArchiveId ?? string.Empty))
        {
            throw new InvalidOperationException("Archive manifest parentArchiveId does not match the header.");
        }

        if (manifest.CreatedUtc != header.CreatedUtc)
        {
            throw new InvalidOperationException("Archive manifest createdUtc does not match the header.");
        }
    }

    private static void ValidateManifestStructure(ArchiveManifest manifest)
    {
        if (manifest is null)
        {
            throw new InvalidOperationException("Archive manifest is missing.");
        }

        if (string.IsNullOrWhiteSpace(manifest.ArchiveId))
        {
            throw new InvalidOperationException("Archive manifest archiveId is required.");
        }

        if (string.IsNullOrWhiteSpace(manifest.ChainId))
        {
            throw new InvalidOperationException("Archive manifest chainId is required.");
        }

        if (string.IsNullOrWhiteSpace(manifest.SourceId))
        {
            throw new InvalidOperationException("Archive manifest sourceId is required.");
        }

        if (manifest.FormatVersion != FormatVersion)
        {
            throw new InvalidOperationException("Archive manifest format version is unsupported.");
        }

        foreach (var file in manifest.Files)
        {
            if (string.IsNullOrWhiteSpace(file.RelativePath))
            {
                throw new InvalidOperationException("Manifest file entry has no relative path.");
            }

            if (file.FileSize < 0)
            {
                throw new InvalidOperationException("Manifest file size cannot be negative.");
            }

            if (string.IsNullOrWhiteSpace(file.Sha256))
            {
                throw new InvalidOperationException("Manifest file entry has no SHA-256 hash.");
            }
        }
    }

    private static ArchiveHeader ParseHeader(byte[] bytes)
    {
        if (bytes.Length < MinimumArchiveLength)
        {
            throw new InvalidOperationException("Archive is truncated.");
        }

        var headerLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
        var payloadLength = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(12, 8));

        var archiveIdLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(20, 2));
        var chainIdLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22, 2));
        var parentArchiveIdLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(24, 2));
        var sourceIdLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(26, 2));
        var createdUtcMs = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(28, 8));
        var kdfIdentifierLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(36, 2));
        var kdfParametersLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(38, 2));
        var saltLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(40, 2));
        var encryptionIdentifierLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(42, 2));
        var nonceLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(44, 2));

        var offset = MinimumHeaderLength;
        var archiveId = ReadUtf8String(bytes, ref offset, archiveIdLength);
        var chainId = ReadUtf8String(bytes, ref offset, chainIdLength);
        var parentArchiveId = ReadUtf8String(bytes, ref offset, parentArchiveIdLength);
        var sourceId = ReadUtf8String(bytes, ref offset, sourceIdLength);
        var kdfIdentifier = ReadUtf8String(bytes, ref offset, kdfIdentifierLength);
        var kdfParameters = ReadBytes(bytes, ref offset, kdfParametersLength);
        var salt = ReadBytes(bytes, ref offset, saltLength);
        var encryptionAlgorithmIdentifier = ReadUtf8String(bytes, ref offset, encryptionIdentifierLength);
        var nonce = ReadBytes(bytes, ref offset, nonceLength);

        if (offset > bytes.Length)
        {
            throw new InvalidOperationException("Archive header fields exceed the file length.");
        }

        return new ArchiveHeader
        {
            Magic = Encoding.ASCII.GetString(bytes.AsSpan(0, 4)),
            FormatVersion = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2)),
            ArchiveType = (ArchiveType)bytes[6],
            ArchiveId = archiveId,
            ChainId = chainId,
            ParentArchiveId = string.IsNullOrEmpty(parentArchiveId) ? null : parentArchiveId,
            SourceId = sourceId,
            CreatedUtc = NormalizeUtcTimestamp(DateTimeOffset.FromUnixTimeMilliseconds(createdUtcMs)),
            KdfIdentifier = kdfIdentifier,
            KdfParameters = kdfParameters,
            Salt = salt,
            EncryptionAlgorithmIdentifier = encryptionAlgorithmIdentifier,
            Nonce = nonce,
            HeaderLength = headerLength,
            PayloadLength = (long)payloadLength
        };
    }

    private static DateTimeOffset NormalizeUtcTimestamp(DateTimeOffset value)
    {
        var utcTicks = value.UtcTicks;
        var millisecondTicks = TimeSpan.TicksPerMillisecond;
        var truncatedTicks = utcTicks - (utcTicks % millisecondTicks);
        return new DateTimeOffset(truncatedTicks, TimeSpan.Zero);
    }

    private static byte[] DeriveKey(string password, byte[] salt, byte[] kdfParameters)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var argon2 = new Argon2id(passwordBytes)
        {
            Salt = salt,
            DegreeOfParallelism = unchecked((int)BitConverter.ToUInt32(kdfParameters.AsSpan(8, 4))),
            Iterations = unchecked((int)BitConverter.ToUInt32(kdfParameters.AsSpan(4, 4))),
            MemorySize = unchecked((int)BitConverter.ToUInt32(kdfParameters.AsSpan(0, 4)))
        };

        return argon2.GetBytes(32);
    }

    private static string ReadUtf8String(byte[] bytes, ref int offset, int length)
    {
        if (length <= 0)
        {
            return string.Empty;
        }

        var value = Encoding.UTF8.GetString(bytes, offset, length);
        offset += length;
        return value;
    }

    private static byte[] ReadBytes(byte[] bytes, ref int offset, int length)
    {
        if (length <= 0)
        {
            return Array.Empty<byte>();
        }

        var value = new byte[length];
        Array.Copy(bytes, offset, value, 0, length);
        offset += length;
        return value;
    }
}
