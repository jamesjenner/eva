using System.Buffers.Binary;

namespace EVA.Core;

public static class ImagePreviewInspection
{
    public const long MaxFileSizeBytes = 20L * 1024 * 1024;
    public const long MaxPixelCount = 50_000_000;
    private const int MaxHeaderBytes = 1024 * 1024;

    public static bool IsSupportedExtension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        return Path.GetExtension(fileName).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif";
    }

    public static bool IsWithinFileSizeLimit(long fileSize) =>
        fileSize >= 0 && fileSize <= MaxFileSizeBytes;

    public static bool IsWithinPixelLimit(int width, int height) =>
        width > 0 && height > 0 && (long)width * height <= MaxPixelCount;

    public static async Task<ImagePreviewInspectionResult> InspectFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!IsSupportedExtension(path))
        {
            return ImagePreviewInspectionResult.Unsupported("Unsupported image extension.");
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);

            if (!IsWithinFileSizeLimit(stream.Length))
            {
                return ImagePreviewInspectionResult.Unsupported("Image exceeds the 20 MB preview limit.");
            }

            var headerLength = (int)Math.Min(stream.Length, MaxHeaderBytes);
            var header = new byte[headerLength];
            await stream.ReadExactlyAsync(header.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (!TryReadDimensions(Path.GetExtension(path), header, out var width, out var height))
            {
                return ImagePreviewInspectionResult.Unsupported("Image header is invalid or unsupported.");
            }

            if (!IsWithinPixelLimit(width, height))
            {
                return ImagePreviewInspectionResult.Unsupported("Image exceeds the 50 megapixel preview limit.");
            }

            return new ImagePreviewInspectionResult(true, width, height, string.Empty);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return ImagePreviewInspectionResult.Unsupported("Image could not be read.");
        }
    }

    private static bool TryReadDimensions(string extension, ReadOnlySpan<byte> header, out int width, out int height)
    {
        width = 0;
        height = 0;
        return extension.ToLowerInvariant() switch
        {
            ".png" => TryReadPngDimensions(header, out width, out height),
            ".gif" => TryReadGifDimensions(header, out width, out height),
            ".bmp" => TryReadBmpDimensions(header, out width, out height),
            ".jpg" or ".jpeg" => TryReadJpegDimensions(header, out width, out height),
            _ => false
        };
    }

    private static bool TryReadPngDimensions(ReadOnlySpan<byte> header, out int width, out int height)
    {
        width = 0;
        height = 0;
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (header.Length < 24 || !header[..8].SequenceEqual(signature) ||
            !header.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return false;
        }

        width = BinaryPrimitives.ReadInt32BigEndian(header.Slice(16, 4));
        height = BinaryPrimitives.ReadInt32BigEndian(header.Slice(20, 4));
        return width > 0 && height > 0;
    }

    private static bool TryReadGifDimensions(ReadOnlySpan<byte> header, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (header.Length < 10 ||
            !(header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8)))
        {
            return false;
        }

        width = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(6, 2));
        height = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(8, 2));
        return width > 0 && height > 0;
    }

    private static bool TryReadBmpDimensions(ReadOnlySpan<byte> header, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (header.Length < 26 || header[0] != (byte)'B' || header[1] != (byte)'M')
        {
            return false;
        }

        var dibHeaderSize = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(14, 4));
        if (dibHeaderSize == 12)
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(18, 2));
            height = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(20, 2));
        }
        else if (dibHeaderSize >= 40 && header.Length >= 26)
        {
            width = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(18, 4));
            var signedHeight = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(22, 4));
            if (signedHeight == int.MinValue)
            {
                return false;
            }

            height = Math.Abs(signedHeight);
        }
        else
        {
            return false;
        }

        return width > 0 && height > 0;
    }

    private static bool TryReadJpegDimensions(ReadOnlySpan<byte> header, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (header.Length < 4 || header[0] != 0xFF || header[1] != 0xD8)
        {
            return false;
        }

        var offset = 2;
        while (offset < header.Length)
        {
            while (offset < header.Length && header[offset] != 0xFF)
            {
                offset++;
            }

            while (offset < header.Length && header[offset] == 0xFF)
            {
                offset++;
            }

            if (offset >= header.Length)
            {
                return false;
            }

            var marker = header[offset++];
            if (marker is 0xD8 or 0xD9 or 0x01 or >= 0xD0 and <= 0xD7)
            {
                continue;
            }

            if (offset + 2 > header.Length)
            {
                return false;
            }

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(offset, 2));
            if (segmentLength < 2 || offset + segmentLength > header.Length)
            {
                return false;
            }

            if (IsStartOfFrame(marker))
            {
                if (segmentLength < 7)
                {
                    return false;
                }

                height = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(offset + 3, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(offset + 5, 2));
                return width > 0 && height > 0;
            }

            offset += segmentLength;
        }

        return false;
    }

    private static bool IsStartOfFrame(byte marker) =>
        marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or
            0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF;
}

public sealed record ImagePreviewInspectionResult(bool IsSupported, int Width, int Height, string Reason)
{
    public static ImagePreviewInspectionResult Unsupported(string reason) => new(false, 0, 0, reason);
}