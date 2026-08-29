using EVA.Core.Interfaces;

namespace EVA.Infrastructure;

public sealed class RetentionEvaluator : IRetentionEvaluator
{
    public Task ApplyRetentionAsync(string archiveDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(archiveDirectory))
        {
            throw new ArgumentException("Archive directory is required.", nameof(archiveDirectory));
        }

        if (!Directory.Exists(archiveDirectory))
        {
            return Task.CompletedTask;
        }

        var archives = Directory.GetFiles(archiveDirectory, "*.eva", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();

        foreach (var archive in archives)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        return Task.CompletedTask;
    }
}
