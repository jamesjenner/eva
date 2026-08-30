using EVA.Core.Models;

namespace EVA.Core.Interfaces;

public interface IConfigStore
{
    Task<BackupConfiguration> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(BackupConfiguration configuration, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(CancellationToken cancellationToken = default);
    Task<BackupConfiguration> UpdateAsync(BackupConfiguration configuration, CancellationToken cancellationToken = default);
}
