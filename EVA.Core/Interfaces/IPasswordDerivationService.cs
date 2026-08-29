namespace EVA.Core.Interfaces;

public interface IPasswordDerivationService
{
    byte[] DeriveKey(string password, byte[] salt, byte[] kdfParameters, CancellationToken cancellationToken = default);
    byte[] CreateKdfParameters();
}
