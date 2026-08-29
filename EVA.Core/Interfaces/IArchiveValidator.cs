using EVA.Core.Models;

namespace EVA.Core.Interfaces;

public interface IArchiveValidator
{
    bool ValidateHeader(ArchiveHeader header);
    bool ValidateManifest(ArchiveManifest manifest, ArchiveHeader header);
    bool ValidateArchiveChain(IReadOnlyCollection<ArchiveHeader> headers, ArchiveChain chain);
}
