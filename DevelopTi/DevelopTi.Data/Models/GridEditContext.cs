namespace DevelopTi.Data.Models;

/// <summary>
/// Contexto que habilita edição na grid de resultado: só existe quando a última consulta rodada na
/// aba foi um SELECT de uma única tabela (sem JOIN) com chave primária conhecida — o suficiente pra
/// montar um WHERE confiável em UPDATE/DELETE.
/// </summary>
public class GridEditContext
{
    /// <summary>Nulo quando a conexão não tem múltiplos bancos (Oracle/MySQL) ou o SQL não qualificou o banco.</summary>
    public string? Database { get; init; }

    public required string Schema { get; init; }
    public required string Table { get; init; }

    /// <summary>Colunas usadas no WHERE do UPDATE/DELETE — nem sempre é a PK de verdade, ver <see cref="IsSyntheticKey"/>.</summary>
    public required IReadOnlyList<string> PrimaryKeyColumns { get; init; }

    /// <summary>True quando a tabela não tem PK nem índice único cadastrado (comum em ERPs — ex: tabelas
    /// TOTVS/Protheus) e o WHERE caiu pro último recurso: comparar todas as colunas da linha. Nesse caso,
    /// se houver linhas duplicadas de verdade, o UPDATE/DELETE pode afetar mais de uma.</summary>
    public bool IsSyntheticKey { get; init; }
}
