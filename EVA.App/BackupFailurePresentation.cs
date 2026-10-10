using EVA.Infrastructure;

namespace EVA.App;

public sealed class BackupFailurePresentation
{
    public string? Message { get; private set; }

    public void Apply(BackupRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Success)
        {
            Message = null;
            return;
        }

        Message = result.FailureType switch
        {
            "SourceSizeLimitExceeded" => "The full backup exceeds the configured source size limit. See the log.",
            nameof(OutOfMemoryException) => "Not enough memory to create the backup. See the log.",
            nameof(IOException) => "A file or disk error stopped the backup. See the log.",
            nameof(UnauthorizedAccessException) => "EVA does not have permission to complete the backup. See the log.",
            _ => "The backup failed. See the log."
        };
    }
}