namespace EVA.Core.Interfaces;

public interface IRetentionEvaluator
{
    Task ApplyRetentionAsync(string archiveDirectory, CancellationToken cancellationToken = default);
}
