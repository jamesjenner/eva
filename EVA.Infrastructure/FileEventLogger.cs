using System.Text;
using EVA.Core.Interfaces;

namespace EVA.Infrastructure;

public sealed class FileEventLogger : IEventLogger
{
    public const long DefaultMaxFileSizeBytes = 5L * 1024 * 1024;
    public const int DefaultRetainedFileCount = 3;

    private readonly string _logDirectory;
    private readonly long _maxFileSizeBytes;
    private readonly int _retainedFileCount;
    private readonly string[] _sensitiveValues;
    private readonly object _sync = new();

    public FileEventLogger(
        string logDirectory,
        long maxFileSizeBytes = DefaultMaxFileSizeBytes,
        int retainedFileCount = DefaultRetainedFileCount,
        IEnumerable<string>? sensitiveValues = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFileSizeBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retainedFileCount);

        _logDirectory = Path.GetFullPath(logDirectory);
        _maxFileSizeBytes = maxFileSizeBytes;
        _retainedFileCount = retainedFileCount;
        _sensitiveValues = (sensitiveValues ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public void LogInformation(string message) => Write("INFO", message);
    public void LogWarning(string message) => Write("WARN", message);
    public void LogError(string message) => Write("ERROR", message);

    public void LogException(Exception exception, string message)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Write("ERROR", $"{message} | {exception.GetType().Name}: {exception.Message}");
    }

    private void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.UtcNow:O} [{level}] {Redact(message)}{Environment.NewLine}";
        var bytes = Encoding.UTF8.GetBytes(line);
        if (bytes.Length > _maxFileSizeBytes)
        {
            bytes = bytes[..(int)_maxFileSizeBytes];
        }

        try
        {
            lock (_sync)
            {
                Directory.CreateDirectory(_logDirectory);
                var currentPath = GetLogPath(0);
                if (File.Exists(currentPath) && new FileInfo(currentPath).Length + bytes.Length > _maxFileSizeBytes)
                {
                    RotateLogs();
                }

                using var stream = new FileStream(currentPath, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(bytes);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string Redact(string message)
    {
        foreach (var value in _sensitiveValues.OrderByDescending(value => value.Length))
        {
            message = message.Replace(value, "[REDACTED]", StringComparison.Ordinal);
        }

        return message;
    }

    private void RotateLogs()
    {
        for (var index = _retainedFileCount - 1; index >= 0; index--)
        {
            var sourcePath = GetLogPath(index);
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            if (index == _retainedFileCount - 1)
            {
                File.Delete(sourcePath);
                continue;
            }

            File.Move(sourcePath, GetLogPath(index + 1), overwrite: true);
        }
    }

    private string GetLogPath(int index) => index == 0
        ? Path.Combine(_logDirectory, "eva.log")
        : Path.Combine(_logDirectory, $"eva.{index}.log");
}