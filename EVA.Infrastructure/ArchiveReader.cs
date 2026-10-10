using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EVA.Core.Interfaces;
using EVA.Core.Models;
using Konscious.Security.Cryptography;

namespace EVA.Infrastructure;

public sealed class ArchiveReader : IArchiveReader, IArchivePayloadExtractor
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

        var payload = await ParsePayloadAsync(plaintext, cancellationToken).ConfigureAwait(false);
        ValidateManifestIdentity(payload.Manifest, header);
        ValidateManifestStructure(payload.Manifest);
        return payload.Manifest;
    }

    public async Task<byte[]> ReadFileContentAsync(string archivePath, string relativePath, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Relative path is required.", nameof(relativePath));
        }

        var bytes = await File.ReadAllBytesAsync(archivePath, cancellationToken).ConfigureAwait(false);
        var header = ParseHeader(bytes);
        ValidateHeaderConsistency(header, bytes.Length);
        var plaintext = DecryptPayload(bytes, header, password);
        var payload = await ParsePayloadAsync(plaintext, cancellationToken, targetRelativePath: relativePath).ConfigureAwait(false);
        ValidateManifestIdentity(payload.Manifest, header);
        ValidateManifestStructure(payload.Manifest);

        var entry = payload.Files.FirstOrDefault(item => string.Equals(item.Entry.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            throw new FileNotFoundException("File entry was not found in the archive.", relativePath);
        }

        return entry.Content;
    }

    public async Task<ArchiveExtractionResult> ExtractFilesAsync(
        string archivePath,
        string password,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            throw new ArgumentException("Archive path is required.", nameof(archivePath));
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("Password is required.", nameof(password));
        }

        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            throw new ArgumentException("Extraction directory is required.", nameof(destinationDirectory));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var bytes = await File.ReadAllBytesAsync(archivePath, cancellationToken).ConfigureAwait(false);
        var header = ParseHeader(bytes);
        ValidateHeaderConsistency(header, bytes.Length);
        var plaintext = DecryptPayload(bytes, header, password);
        cancellationToken.ThrowIfCancellationRequested();
        var payload = await ParsePayloadAsync(plaintext, cancellationToken, extractionDirectory: destinationDirectory)
            .ConfigureAwait(false);
        ValidateManifestIdentity(payload.Manifest, header);
        ValidateManifestStructure(payload.Manifest);
        return new ArchiveExtractionResult(payload.Manifest.ArchiveId, payload.SkippedEntries);
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

    private static byte[] DecryptPayload(byte[] bytes, ArchiveHeader header, string password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("Password is required.", nameof(password));
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

        return plaintext;
    }

    private static async Task<PayloadData> ParsePayloadAsync(
        byte[] encryptedPayload,
        CancellationToken cancellationToken,
        string? extractionDirectory = null,
        string? targetRelativePath = null)
    {
        using var compressedStream = new MemoryStream(encryptedPayload, writable: false);
        using var decompressor = new GZipStream(compressedStream, CompressionMode.Decompress);
        using var reader = new BinaryReader(decompressor, Encoding.UTF8, leaveOpen: true);
        var payloadVersion = reader.ReadUInt16();
        if (payloadVersion != 1)
        {
            throw new InvalidOperationException("Payload format version is unsupported.");
        }

        var manifestLength = ReadLength(reader, "manifest");
        var manifestJson = Encoding.UTF8.GetString(ReadExact(reader, manifestLength));
        var manifest = JsonSerializer.Deserialize<ArchiveManifest>(manifestJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Archive manifest is missing.");

        var files = new List<PayloadFile>();
        var skippedEntries = new List<string>();
        var fileCount = ReadCount(reader, "file");
        for (var index = 0; index < fileCount; index++)
        {
            var relativePath = ReadString(reader, sizeof(ushort));
            var operation = ReadOperation(reader);
            var fileSize = reader.ReadInt64();
            var lastModifiedUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.ReadInt64());
            var sha256 = ReadString(reader, sizeof(byte));
            var contentLength = reader.ReadInt64();
            if (contentLength < 0 || contentLength > int.MaxValue || operation == FileOperation.Deleted && contentLength != 0)
            {
                throw new InvalidOperationException("Payload file content length is invalid.");
            }

            var entry = new FileEntry
            {
                RelativePath = relativePath,
                Operation = operation,
                FileSize = fileSize,
                LastModifiedUtc = lastModifiedUtc,
                Sha256 = sha256
            };
            byte[] content = Array.Empty<byte>();

            if (operation != FileOperation.Deleted)
            {
                if (extractionDirectory is not null)
                {
                    if (TryGetExtractionPath(extractionDirectory, relativePath, out var extractionPath))
                    {
                        var parent = Path.GetDirectoryName(extractionPath);
                        if (!string.IsNullOrEmpty(parent))
                        {
                            Directory.CreateDirectory(parent);
                        }

                        await using var output = new FileStream(
                            extractionPath,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None,
                            64 * 1024,
                            useAsync: true);
                        if (contentLength > 0)
                        {
                            await CopyExactlyAsync(reader.BaseStream, output, contentLength, cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        skippedEntries.Add(relativePath);
                        await SkipExactlyAsync(reader.BaseStream, contentLength, cancellationToken).ConfigureAwait(false);
                    }
                }
                else if (targetRelativePath is not null &&
                         string.Equals(relativePath, targetRelativePath, StringComparison.OrdinalIgnoreCase))
                {
                    if (contentLength > 0)
                    {
                        content = ReadExact(reader, (int)contentLength);
                    }
                }
                else
                {
                    await SkipExactlyAsync(reader.BaseStream, contentLength, cancellationToken).ConfigureAwait(false);
                }
            }

            files.Add(new PayloadFile { Entry = entry, Content = content });
        }

        var directories = new List<DirectoryEntry>();
        var directoryCount = ReadCount(reader, "directory");
        for (var index = 0; index < directoryCount; index++)
        {
            directories.Add(new DirectoryEntry
            {
                RelativePath = ReadString(reader, sizeof(ushort)),
                Operation = ReadOperation(reader),
                LastModifiedUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.ReadInt64())
            });
        }

        if (reader.ReadUInt32() != 0x45564130)
        {
            throw new InvalidOperationException("Payload end marker is invalid.");
        }

        if (reader.BaseStream.ReadByte() != -1)
        {
            throw new InvalidOperationException("Payload contains trailing data.");
        }

        manifest.Files = files.Select(file => file.Entry).ToList();
        manifest.Directories = directories;
        return new PayloadData(manifest, files, skippedEntries);
    }

    private static async Task CopyExactlyAsync(
        Stream input,
        Stream output,
        long length,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var remaining = length;
        while (remaining > 0)
        {
            var requested = (int)Math.Min(buffer.Length, remaining);
            var read = await input.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidOperationException("Payload is truncated.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
    }

    private static async Task SkipExactlyAsync(Stream input, long length, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var remaining = length;
        while (remaining > 0)
        {
            var requested = (int)Math.Min(buffer.Length, remaining);
            var read = await input.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidOperationException("Payload is truncated.");
            }

            remaining -= read;
        }
    }

    private static bool TryGetExtractionPath(string rootDirectory, string relativePath, out string extractionPath)
    {
        extractionPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) ||
            relativePath.StartsWith('/') || relativePath.StartsWith('\\'))
        {
            throw new InvalidDataException("Archive contains a rooted or empty file path.");
        }

        var segments = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException("Archive contains an unsafe relative file path.");
        }

        if (segments.Any(IsInvalidWindowsPathSegment))
        {
            return false;
        }

        var normalized = Path.Combine(segments);
        var root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        extractionPath = Path.GetFullPath(Path.Combine(root, normalized));
        var rootPrefix = root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!extractionPath.StartsWith(rootPrefix, comparison))
        {
            throw new InvalidDataException("Archive contains a path outside its extraction directory.");
        }

        return true;
    }

    private static bool IsInvalidWindowsPathSegment(string segment)
    {
        if (segment.EndsWith('.') || segment.EndsWith(' ') || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            segment.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0 ||
            segment.Any(char.IsControl))
        {
            return true;
        }

        var deviceName = segment.Split('.')[0].TrimEnd(' ', '.');
        if (deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return deviceName.Length == 4 &&
               (deviceName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                deviceName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
               deviceName[3] is >= '1' and <= '9';
    }

    private static FileOperation ReadOperation(BinaryReader reader)
    {
        var value = reader.ReadByte();
        if (!Enum.IsDefined(typeof(FileOperation), (int)value))
        {
            throw new InvalidOperationException("Payload file operation is invalid.");
        }

        return (FileOperation)value;
    }

    private static string ReadString(BinaryReader reader, int lengthSize)
    {
        var length = lengthSize == sizeof(byte) ? reader.ReadByte() : reader.ReadUInt16();
        return Encoding.UTF8.GetString(ReadExact(reader, length));
    }

    private static int ReadLength(BinaryReader reader, string name)
    {
        var length = reader.ReadInt32();
        if (length < 0) throw new InvalidOperationException($"Payload {name} length is invalid.");
        return length;
    }

    private static int ReadCount(BinaryReader reader, string name)
    {
        var count = reader.ReadInt32();
        if (count < 0) throw new InvalidOperationException($"Payload {name} count is invalid.");
        return count;
    }

    private static byte[] ReadExact(BinaryReader reader, int length)
    {
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new InvalidOperationException("Payload is truncated.");
        return bytes;
    }

    private sealed record PayloadData(
        ArchiveManifest Manifest,
        List<PayloadFile> Files,
        IReadOnlyList<string> SkippedEntries);

    private sealed class PayloadFile
    {
        public FileEntry Entry { get; set; } = new();
        public byte[] Content { get; set; } = Array.Empty<byte>();
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

            if (file.Operation != FileOperation.Deleted && string.IsNullOrWhiteSpace(file.Sha256))
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
