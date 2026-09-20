using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EVA.Core.Interfaces;
using EVA.Core.Models;
using Konscious.Security.Cryptography;

namespace EVA.Infrastructure;

// Existing .eva files use the superseded JSON payload and must be deleted before testing this format.
public sealed class ArchiveWriter : IArchiveWriter
{
    private const string Magic = "EVA1";
    private const ushort FormatVersion = 1;
    private const string KdfIdentifier = "argon2id";
    private const string EncryptionAlgorithmIdentifier = "AES-256-GCM";
    private const int SaltLength = 16;
    private const int NonceLength = 12;
    private const int AuthenticationTagLength = 16;
    private const ushort PayloadFormatVersion = 1;
    private const uint EndOfPayloadMarker = 0x45564130;
    private readonly string? _sourceDirectory;

    public ArchiveWriter(string? sourceDirectory = null)
    {
        _sourceDirectory = string.IsNullOrWhiteSpace(sourceDirectory) ? null : Path.GetFullPath(sourceDirectory);
    }

    public async Task WriteArchiveAsync(
        ArchiveManifest manifest,
        IReadOnlyCollection<FileEntry> fileEntries,
        string destinationPath,
        string password,
        CancellationToken cancellationToken = default)
    {
        await WriteArchiveAsync(
            manifest,
            fileEntries,
            new List<DirectoryEntry>(),
            destinationPath,
            password,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteArchiveAsync(
        ArchiveManifest manifest,
        IReadOnlyCollection<FileEntry> fileEntries,
        List<DirectoryEntry> directoryEntries,
        string destinationPath,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("Destination path is required.", nameof(destinationPath));
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("Password is required.", nameof(password));
        }

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), "eva", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        var tempPath = Path.Combine(tempDirectory, $"{Guid.NewGuid():N}.tmp");

        try
        {
            var archiveType = manifest.ArchiveType;
            var archiveId = manifest.ArchiveId;
            var chainId = manifest.ChainId;
            var parentArchiveId = manifest.ParentArchiveId ?? string.Empty;
            var sourceId = manifest.SourceId;
            var createdUtc = NormalizeUtcTimestamp(manifest.CreatedUtc);
            manifest.CreatedUtc = createdUtc;
            manifest.Files.Clear();
            manifest.Files.AddRange(fileEntries);
            manifest.Directories.Clear();
            manifest.Directories.AddRange(directoryEntries);

            var payload = await BuildPayloadAsync(manifest, fileEntries, directoryEntries, cancellationToken).ConfigureAwait(false);
            var salt = RandomNumberGenerator.GetBytes(SaltLength);
            var nonce = RandomNumberGenerator.GetBytes(NonceLength);
            var kdfParameters = CreateKdfParameters();
            var key = DeriveKey(password, salt, kdfParameters);

            var header = new ArchiveHeader
            {
                Magic = Magic,
                FormatVersion = FormatVersion,
                ArchiveType = archiveType,
                ArchiveId = archiveId,
                ChainId = chainId,
                ParentArchiveId = parentArchiveId,
                SourceId = sourceId,
                CreatedUtc = createdUtc,
                KdfIdentifier = KdfIdentifier,
                KdfParameters = kdfParameters,
                Salt = salt,
                EncryptionAlgorithmIdentifier = EncryptionAlgorithmIdentifier,
                Nonce = nonce,
                HeaderLength = 0,
                PayloadLength = payload.Length
            };

            var headerBytes = SerializeHeader(header);
            header.HeaderLength = headerBytes.Length;
            var finalHeaderBytes = SerializeHeader(header);

            var aad = finalHeaderBytes;
            var encrypted = EncryptPayload(payload, key, nonce, aad);
            var archiveBytes = Combine(finalHeaderBytes, encrypted);

            await File.WriteAllBytesAsync(tempPath, archiveBytes, cancellationToken).ConfigureAwait(false);
            VerifyArchive(tempPath, password);

            var finalArchivePath = destinationPath;
            if (File.Exists(finalArchivePath))
            {
                File.Delete(finalArchivePath);
            }

            File.Move(tempPath, finalArchivePath, overwrite: true);

            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }

            throw;
        }
    }

    private static DateTimeOffset NormalizeUtcTimestamp(DateTimeOffset value)
    {
        var utcTicks = value.UtcTicks;
        var millisecondTicks = TimeSpan.TicksPerMillisecond;
        var truncatedTicks = utcTicks - (utcTicks % millisecondTicks);
        return new DateTimeOffset(truncatedTicks, TimeSpan.Zero);
    }

    private async Task<byte[]> BuildPayloadAsync(
        ArchiveManifest manifest,
        IReadOnlyCollection<FileEntry> fileEntries,
        IReadOnlyCollection<DirectoryEntry> directoryEntries,
        CancellationToken cancellationToken)
    {
        var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
        });
        var manifestBytes = Encoding.UTF8.GetBytes(manifestJson);

        using var uncompressed = new MemoryStream();
        using (var writer = new BinaryWriter(uncompressed, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(PayloadFormatVersion);
            writer.Write(manifestBytes.Length);
            writer.Write(manifestBytes);
            writer.Write(fileEntries.Count);

            foreach (var entry in fileEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteString(writer, entry.RelativePath, sizeof(ushort));
                writer.Write((byte)entry.Operation);
                writer.Write(entry.FileSize);
                writer.Write(NormalizeUtcTimestamp(entry.LastModifiedUtc).ToUnixTimeMilliseconds());
                WriteString(writer, entry.Sha256, sizeof(byte));

                var content = entry.Operation == FileOperation.Deleted
                    ? Array.Empty<byte>()
                    : await ReadSourceBytesAsync(entry, cancellationToken).ConfigureAwait(false);
                writer.Write((long)content.Length);
                writer.Write(content);
            }

            writer.Write(directoryEntries.Count);
            foreach (var entry in directoryEntries)
            {
                WriteString(writer, entry.RelativePath, sizeof(ushort));
                writer.Write((byte)entry.Operation);
                writer.Write(NormalizeUtcTimestamp(entry.LastModifiedUtc ?? DateTimeOffset.UnixEpoch).ToUnixTimeMilliseconds());
            }

            writer.Write(EndOfPayloadMarker);
        }

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            uncompressed.Position = 0;
            await uncompressed.CopyToAsync(gzip, cancellationToken).ConfigureAwait(false);
        }

        return output.ToArray();
    }

    private async Task<byte[]> ReadSourceBytesAsync(FileEntry entry, CancellationToken cancellationToken)
    {
        var sourcePath = _sourceDirectory is null
            ? entry.ContentRef?.ObjectId
            : Path.Combine(_sourceDirectory, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            if (_sourceDirectory is null)
            {
                return entry.FileSize > int.MaxValue ? throw new InvalidOperationException("Archive entry is too large.") : new byte[(int)entry.FileSize];
            }

            throw new FileNotFoundException($"Source file was not found for archive entry '{entry.RelativePath}'.", sourcePath);
        }

        return await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
    }

    private static void WriteString(BinaryWriter writer, string? value, int lengthSize)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        if (lengthSize == sizeof(byte))
        {
            if (bytes.Length > byte.MaxValue) throw new InvalidOperationException("Payload string is too long.");
            writer.Write((byte)bytes.Length);
        }
        else
        {
            if (bytes.Length > ushort.MaxValue) throw new InvalidOperationException("Payload string is too long.");
            writer.Write((ushort)bytes.Length);
        }

        writer.Write(bytes);
    }

    private static byte[] CreateKdfParameters()
    {
        var memoryCost = 65536u;
        var timeCost = 3u;
        var parallelism = 4u;
        var outputLength = 32u;

        var buffer = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), memoryCost);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4, 4), timeCost);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8, 4), parallelism);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12, 4), outputLength);
        return buffer;
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

        var bytes = argon2.GetBytes(32);
        return bytes;
    }

    private static byte[] SerializeHeader(ArchiveHeader header)
    {
        var archiveIdBytes = Encoding.UTF8.GetBytes(header.ArchiveId ?? string.Empty);
        var chainIdBytes = Encoding.UTF8.GetBytes(header.ChainId ?? string.Empty);
        var parentArchiveIdBytes = Encoding.UTF8.GetBytes(header.ParentArchiveId ?? string.Empty);
        var sourceIdBytes = Encoding.UTF8.GetBytes(header.SourceId ?? string.Empty);
        var kdfIdentifierBytes = Encoding.UTF8.GetBytes(header.KdfIdentifier ?? string.Empty);
        var kdfParametersBytes = header.KdfParameters ?? Array.Empty<byte>();
        var saltBytes = header.Salt ?? Array.Empty<byte>();
        var encryptionIdentifierBytes = Encoding.UTF8.GetBytes(header.EncryptionAlgorithmIdentifier ?? string.Empty);
        var nonceBytes = header.Nonce ?? Array.Empty<byte>();

        var createdUtcEpochMs = NormalizeUtcTimestamp(header.CreatedUtc).ToUnixTimeMilliseconds();

        var length = 4 + 2 + 1 + 1 + 4 + 8
            + 2 + 2 + 2 + 2
            + 8
            + 2 + 2 + 2 + 2 + 2
            + archiveIdBytes.Length + chainIdBytes.Length + parentArchiveIdBytes.Length + sourceIdBytes.Length
            + kdfIdentifierBytes.Length + kdfParametersBytes.Length + saltBytes.Length + encryptionIdentifierBytes.Length + nonceBytes.Length;

        var buffer = new byte[length];
        var offset = 0;

        Encoding.ASCII.GetBytes(Magic).CopyTo(buffer, 0);
        offset += 4;

        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), header.FormatVersion);
        offset += 2;

        buffer[offset++] = (byte)header.ArchiveType;
        buffer[offset++] = 0;

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), (uint)header.HeaderLength);
        offset += 4;

        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset, 8), (ulong)header.PayloadLength);
        offset += 8;

        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), (ushort)archiveIdBytes.Length);
        offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), (ushort)chainIdBytes.Length);
        offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), (ushort)parentArchiveIdBytes.Length);
        offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), (ushort)sourceIdBytes.Length);
        offset += 2;

        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset, 8), createdUtcEpochMs);
        offset += 8;

        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), (ushort)kdfIdentifierBytes.Length);
        offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), (ushort)kdfParametersBytes.Length);
        offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), (ushort)saltBytes.Length);
        offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), (ushort)encryptionIdentifierBytes.Length);
        offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), (ushort)nonceBytes.Length);
        offset += 2;

        archiveIdBytes.CopyTo(buffer, offset);
        offset += archiveIdBytes.Length;
        chainIdBytes.CopyTo(buffer, offset);
        offset += chainIdBytes.Length;
        parentArchiveIdBytes.CopyTo(buffer, offset);
        offset += parentArchiveIdBytes.Length;
        sourceIdBytes.CopyTo(buffer, offset);
        offset += sourceIdBytes.Length;
        kdfIdentifierBytes.CopyTo(buffer, offset);
        offset += kdfIdentifierBytes.Length;
        kdfParametersBytes.CopyTo(buffer, offset);
        offset += kdfParametersBytes.Length;
        saltBytes.CopyTo(buffer, offset);
        offset += saltBytes.Length;
        encryptionIdentifierBytes.CopyTo(buffer, offset);
        offset += encryptionIdentifierBytes.Length;
        nonceBytes.CopyTo(buffer, offset);

        return buffer;
    }

    private static byte[] Combine(byte[] left, byte[] right)
    {
        var combined = new byte[left.Length + right.Length];
        left.CopyTo(combined, 0);
        right.CopyTo(combined, left.Length);
        return combined;
    }

    private static byte[] EncryptPayload(byte[] payload, byte[] key, byte[] nonce, byte[] aad)
    {
        var ciphertext = new byte[payload.Length];
        var tag = new byte[AuthenticationTagLength];
        using var aes = new AesGcm(key, AuthenticationTagLength);
        aes.Encrypt(nonce, payload, ciphertext, tag, aad);

        var result = new byte[ciphertext.Length + tag.Length];
        ciphertext.CopyTo(result, 0);
        tag.CopyTo(result, ciphertext.Length);
        return result;
    }

    private static void VerifyArchive(string archivePath, string password)
    {
        var reader = new ArchiveReader();
        _ = reader.ReadManifestAsync(archivePath, password).GetAwaiter().GetResult();
    }
}
