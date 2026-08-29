namespace EVA.Core.Interfaces;

public interface ICompressionProvider
{
    byte[] Compress(byte[] input, CancellationToken cancellationToken = default);
    byte[] Decompress(byte[] input, CancellationToken cancellationToken = default);
}
