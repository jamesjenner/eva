using System.Text.Json;

namespace EVA.Infrastructure;

public sealed class SecondaryDestinationRetryQueue
{
    private readonly string _queuePath;

    public SecondaryDestinationRetryQueue(string? queuePath = null)
    {
        _queuePath = queuePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EVA", "retry-queue.json");
        var directory = Path.GetDirectoryName(_queuePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public async Task EnqueueAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            throw new ArgumentException("Archive path is required.", nameof(archivePath));
        }

        var queue = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (queue.Contains(archivePath, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        queue.Add(archivePath);
        await SaveAsync(queue, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            return;
        }

        var queue = await LoadAsync(cancellationToken).ConfigureAwait(false);
        queue.Remove(archivePath);
        await SaveAsync(queue, cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<string>> GetPendingAsync(CancellationToken cancellationToken = default)
    {
        var queue = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return queue.ToList();
    }

    public async Task RetryAsync(Func<string, CancellationToken, Task<bool>> retryAction, CancellationToken cancellationToken = default)
    {
        if (retryAction is null)
        {
            throw new ArgumentNullException(nameof(retryAction));
        }

        var pending = await GetPendingAsync(cancellationToken).ConfigureAwait(false);
        foreach (var archivePath in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var success = await retryAction(archivePath, cancellationToken).ConfigureAwait(false);
            if (success)
            {
                await RemoveAsync(archivePath, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<HashSet<string>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_queuePath))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        await using var stream = File.OpenRead(_queuePath);
        var entries = await JsonSerializer.DeserializeAsync<List<string>>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new HashSet<string>(entries ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
    }

    private async Task SaveAsync(HashSet<string> queue, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(_queuePath);
        await JsonSerializer.SerializeAsync(stream, queue.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(), cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
