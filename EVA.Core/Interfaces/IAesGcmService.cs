namespace EVA.Core.Interfaces;

public interface IAesGcmService
{
    byte[] Encrypt(byte[] plaintext, byte[] key, byte[] nonce, byte[] associatedData, CancellationToken cancellationToken = default);
    byte[] Decrypt(byte[] ciphertext, byte[] key, byte[] nonce, byte[] associatedData, CancellationToken cancellationToken = default);
}
