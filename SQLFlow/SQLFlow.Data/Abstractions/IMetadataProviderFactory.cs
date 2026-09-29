using SQLFlow.Data.Models;

namespace SQLFlow.Data.Abstractions;

public interface IMetadataProviderFactory
{
    IMetadataProvider GetProvider(DatabaseKind kind);
}
