using DevelopTi.Data.Models;

namespace DevelopTi.Data.Abstractions;

public interface IMetadataProviderFactory
{
    IMetadataProvider GetProvider(DatabaseKind kind);
}
