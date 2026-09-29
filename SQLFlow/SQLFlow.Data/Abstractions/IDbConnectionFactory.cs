using System.Data.Common;
using SQLFlow.Data.Models;

namespace SQLFlow.Data.Abstractions;

public interface IDbConnectionFactory
{
    /// <summary>Cria (sem abrir) a conexão ADO.NET correta para o tipo de banco do perfil.</summary>
    DbConnection CreateConnection(ConnectionProfile profile, string password);
}
