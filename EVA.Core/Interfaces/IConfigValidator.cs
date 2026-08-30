using EVA.Core.Models;

namespace EVA.Core.Interfaces;

public interface IConfigValidator
{
    void Validate(BackupConfiguration configuration);
}