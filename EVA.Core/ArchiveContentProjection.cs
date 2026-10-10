using System.Text;
using EVA.Core.Models;

namespace EVA.Core;

public static class ArchiveContentProjection
{
    public const int MaxPreviewBytes = 256 * 1024;
    private const int BinaryScanBytes = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UnicodeEncoding StrictUtf16LittleEndian = new(false, true, true);
    private static readonly UnicodeEncoding StrictUtf16BigEndian = new(true, true, true);
    private static readonly UTF32Encoding StrictUtf32LittleEndian = new(false, true, true);
    private static readonly UTF32Encoding StrictUtf32BigEndian = new(true, true, true);

    public static IReadOnlyList<ArchiveContentNode> BuildTree(
        IEnumerable<FileEntry> files,
        IEnumerable<DirectoryEntry> directories,
        IEnumerable<DeletedEntry>? deletedEntries = null)
    {
        var roots = new Dictionary<string, MutableNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in directories)
        {
            AddPath(roots, directory.RelativePath, isDirectory: true,
                directory.Operation == FileOperation.Deleted, directory.RelativePath);
        }

        foreach (var file in files)
        {
            AddPath(roots, file.RelativePath, isDirectory: false,
                file.Operation == FileOperation.Deleted, file.RelativePath);
        }

        if (deletedEntries is not null)
        {
            foreach (var deleted in deletedEntries)
            {
                AddPath(roots, deleted.RelativePath, isDirectory: false, isDeleted: true, deleted.RelativePath);
            }
        }

        return SortNodes(roots.Values);
    }

    public static string GetArchiveTypeLabel(string fileName, ArchiveType headerType, bool isManual = false)
    {
        var type = GetArchiveTypeFromFilename(fileName) ?? headerType;

        if (type == ArchiveType.Full && isManual)
        {
            return "Full (Manual)";
        }

        return type == ArchiveType.Full ? "Full" : "Incremental";
    }

    public static string GetArchiveTypeLabelFromFilename(string fileName) =>
        GetArchiveTypeFromFilename(fileName) switch
        {
            ArchiveType.Full => "Full",
            ArchiveType.Incremental => "Incremental",
            _ => "Unknown"
        };

    private static ArchiveType? GetArchiveTypeFromFilename(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (name.Contains("_full_", StringComparison.OrdinalIgnoreCase))
        {
            return ArchiveType.Full;
        }

        return name.Contains("_incremental_", StringComparison.OrdinalIgnoreCase)
            ? ArchiveType.Incremental
            : null;
    }

    public static TextPreview CreateTextPreview(byte[] content, int maxBytes = MaxPreviewBytes)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        Encoding? bomEncoding = null;
        var bomLength = 0;
        var codeUnitSize = 1;
        var isUtf16BigEndian = false;

        if (content.Length >= 4 && content[0] == 0xFF && content[1] == 0xFE && content[2] == 0x00 && content[3] == 0x00)
        {
            bomEncoding = StrictUtf32LittleEndian;
            bomLength = 4;
            codeUnitSize = 4;
        }
        else if (content.Length >= 4 && content[0] == 0x00 && content[1] == 0x00 && content[2] == 0xFE && content[3] == 0xFF)
        {
            bomEncoding = StrictUtf32BigEndian;
            bomLength = 4;
            codeUnitSize = 4;
        }
        else if (content.Length >= 2 && content[0] == 0xFF && content[1] == 0xFE)
        {
            bomEncoding = StrictUtf16LittleEndian;
            bomLength = 2;
            codeUnitSize = 2;
        }
        else if (content.Length >= 2 && content[0] == 0xFE && content[1] == 0xFF)
        {
            bomEncoding = StrictUtf16BigEndian;
            bomLength = 2;
            codeUnitSize = 2;
            isUtf16BigEndian = true;
        }
        else if (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
        {
            bomEncoding = StrictUtf8;
            bomLength = 3;
        }

        if (bomEncoding is not null)
        {
            return CreateBomTextPreview(content, maxBytes, bomEncoding, bomLength, codeUnitSize, isUtf16BigEndian);
        }

        var scanLength = Math.Min(content.Length, BinaryScanBytes);
        if (content.AsSpan(0, scanLength).Contains((byte)0))
        {
            return TextPreview.Unsupported;
        }

        var length = Math.Min(content.Length, maxBytes);
        var truncated = content.Length > maxBytes;
        string text;

        try
        {
            text = StrictUtf8.GetString(content, 0, length);
        }
        catch (DecoderFallbackException exception)
        {
            if (!truncated || length - exception.Index > 3)
            {
                return TextPreview.Unsupported;
            }

            text = StrictUtf8.GetString(content, 0, exception.Index);
        }

        if (HasUnsupportedControlCharacters(text))
        {
            return TextPreview.Unsupported;
        }

        return new TextPreview(true, text, truncated);
    }

    private static TextPreview CreateBomTextPreview(
        byte[] content,
        int maxBytes,
        Encoding encoding,
        int bomLength,
        int codeUnitSize,
        bool isUtf16BigEndian)
    {
        var payloadLength = content.Length - bomLength;
        if (payloadLength % codeUnitSize != 0)
        {
            return TextPreview.Unsupported;
        }

        try
        {
            _ = encoding.GetCharCount(content, bomLength, payloadLength);
        }
        catch (DecoderFallbackException)
        {
            return TextPreview.Unsupported;
        }

        var truncated = content.Length > maxBytes;
        var previewLength = Math.Max(0, Math.Min(content.Length, maxBytes) - bomLength);
        previewLength -= previewLength % codeUnitSize;

        if (truncated && codeUnitSize == 2 && previewLength >= 2)
        {
            var lastUnitOffset = bomLength + previewLength - 2;
            var lastCodeUnit = isUtf16BigEndian
                ? (content[lastUnitOffset] << 8) | content[lastUnitOffset + 1]
                : content[lastUnitOffset] | (content[lastUnitOffset + 1] << 8);
            if (char.IsHighSurrogate((char)lastCodeUnit))
            {
                previewLength -= 2;
            }
        }

        string text;
        var trailingBytesToDrop = truncated && codeUnitSize == 1 ? 3 : 0;
        for (var droppedBytes = 0; ; droppedBytes++)
        {
            try
            {
                text = encoding.GetString(content, bomLength, previewLength - droppedBytes);
                break;
            }
            catch (DecoderFallbackException) when (droppedBytes < trailingBytesToDrop)
            {
            }
            catch (DecoderFallbackException)
            {
                return TextPreview.Unsupported;
            }
        }

        if (HasUnsupportedControlCharacters(text))
        {
            return TextPreview.Unsupported;
        }

        return new TextPreview(true, text, truncated);
    }

    private static bool HasUnsupportedControlCharacters(string text) =>
        text.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t' and not '\f');

    private static void AddPath(
        IDictionary<string, MutableNode> roots,
        string path,
        bool isDirectory,
        bool isDeleted,
        string archivePath)
    {
        var segments = NormalizeSegments(path);
        if (segments.Length == 0)
        {
            return;
        }

        var current = roots;
        MutableNode? node = null;
        for (var index = 0; index < segments.Length; index++)
        {
            var segmentIsDirectory = index < segments.Length - 1 || isDirectory;
            if (!current.TryGetValue(segments[index], out node))
            {
                node = new MutableNode(segments[index], segmentIsDirectory);
                current.Add(segments[index], node);
            }
            else if (node.IsDirectory != segmentIsDirectory)
            {
                throw new InvalidDataException("Archive entries contain a file and directory with the same path.");
            }

            if (index == segments.Length - 1)
            {
                node.IsDeleted |= isDeleted;
                if (!segmentIsDirectory)
                {
                    node.ArchivePath = archivePath;
                }
            }

            current = node.Children;
        }
    }

    private static string[] NormalizeSegments(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/') || Path.IsPathRooted(path))
        {
            throw new InvalidDataException("Archive entry path must be relative.");
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException("Archive entry path contains an invalid relative segment.");
        }

        return segments;
    }

    private static IReadOnlyList<ArchiveContentNode> SortNodes(IEnumerable<MutableNode> nodes)
    {
        return nodes
            .OrderByDescending(node => node.IsDirectory)
            .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.Name, StringComparer.Ordinal)
            .Select(node => new ArchiveContentNode(
                node.Name,
                node.ArchivePath,
                node.IsDirectory,
                node.IsDeleted,
                SortNodes(node.Children.Values)))
            .ToArray();
    }

    private sealed class MutableNode(string name, bool isDirectory)
    {
        public string Name { get; } = name;
        public bool IsDirectory { get; } = isDirectory;
        public bool IsDeleted { get; set; }
        public string? ArchivePath { get; set; }
        public Dictionary<string, MutableNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}

public sealed class ArchiveContentNode(
    string name,
    string? archivePath,
    bool isDirectory,
    bool isDeleted,
    IReadOnlyList<ArchiveContentNode> children)
{
    public string Name { get; } = name;
    public string? ArchivePath { get; } = archivePath;
    public bool IsDirectory { get; } = isDirectory;
    public bool IsDeleted { get; } = isDeleted;
    public IReadOnlyList<ArchiveContentNode> Children { get; } = children;
}

public sealed record TextPreview(bool IsSupported, string Text, bool IsTruncated)
{
    public static TextPreview Unsupported { get; } = new(false, string.Empty, false);
}