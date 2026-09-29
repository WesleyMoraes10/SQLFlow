using SQLFlow.Data.Abstractions;
using SQLFlow.Data.Models;

namespace SQLFlow.Data.Services;

/// <summary>Monta UPDATE/DELETE parametrizados a partir de uma edição feita na grid — o WHERE sempre
/// usa os valores ORIGINAIS da linha (lidos antes da edição), nunca os editados, mesmo quando a
/// própria coluna de chave primária foi alterada.</summary>
public static class GridChangeSqlBuilder
{
    public static (string Sql, Dictionary<string, object?> Parameters) BuildUpdate(
        IMetadataProvider provider,
        GridEditContext context,
        IReadOnlyDictionary<string, object?> originalRow,
        IReadOnlyDictionary<string, object?> changedColumns)
    {
        var parameters = new Dictionary<string, object?>();
        var setParts = new List<string>();
        var index = 0;

        foreach (var (column, newValue) in changedColumns)
        {
            // Coluna identity/auto-incremento nunca aceita UPDATE (a UI já bloqueia editar essa célula —
            // isso aqui é só reforço, pra não montar um SET inválido caso um valor pendente chegue mesmo assim).
            if (context.IdentityColumns.Contains(column, StringComparer.OrdinalIgnoreCase)) continue;

            var name = provider.ParameterName(index);
            setParts.Add($"{provider.QuoteIdentifier(column)} = {provider.ParameterToken(index)}");
            parameters[name] = newValue ?? DBNull.Value;
            index++;
        }

        var whereClause = BuildPrimaryKeyWhere(provider, context, originalRow, parameters, ref index);
        var sql = $"UPDATE {QualifiedTable(provider, context)} SET {string.Join(", ", setParts)} WHERE {whereClause}";
        return (sql, parameters);
    }

    public static (string Sql, Dictionary<string, object?> Parameters) BuildInsert(
        IMetadataProvider provider,
        GridEditContext context,
        IReadOnlyDictionary<string, object?> newRow)
    {
        var parameters = new Dictionary<string, object?>();
        var columns = new List<string>();
        var placeholders = new List<string>();
        var index = 0;

        foreach (var (column, value) in newRow)
        {
            // Coluna identity/auto-incremento: o banco gera o valor sozinho. Mandar ela no INSERT (mesmo
            // com valor NULL) é rejeitado (ex: SQL Server "IDENTITY_INSERT está OFF").
            if (context.IdentityColumns.Contains(column, StringComparer.OrdinalIgnoreCase)) continue;

            var name = provider.ParameterName(index);
            columns.Add(provider.QuoteIdentifier(column));
            placeholders.Add(provider.ParameterToken(index));
            parameters[name] = value ?? DBNull.Value;
            index++;
        }

        var sql = $"INSERT INTO {QualifiedTable(provider, context)} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", placeholders)})";
        return (sql, parameters);
    }

    public static (string Sql, Dictionary<string, object?> Parameters) BuildDelete(
        IMetadataProvider provider,
        GridEditContext context,
        IReadOnlyDictionary<string, object?> originalRow)
    {
        var parameters = new Dictionary<string, object?>();
        var index = 0;
        var whereClause = BuildPrimaryKeyWhere(provider, context, originalRow, parameters, ref index);
        var sql = $"DELETE FROM {QualifiedTable(provider, context)} WHERE {whereClause}";
        return (sql, parameters);
    }

    private static string BuildPrimaryKeyWhere(
        IMetadataProvider provider,
        GridEditContext context,
        IReadOnlyDictionary<string, object?> originalRow,
        Dictionary<string, object?> parameters,
        ref int index)
    {
        var whereParts = new List<string>();
        foreach (var pk in context.PrimaryKeyColumns)
        {
            var value = originalRow.TryGetValue(pk, out var v) ? v : null;
            if (value is null)
            {
                // "= @param" nunca bate com NULL em SQL — precisa de IS NULL. Comum de acontecer no
                // fallback de "todas as colunas como chave" (GridEditContext.IsSyntheticKey).
                whereParts.Add($"{provider.QuoteIdentifier(pk)} IS NULL");
                continue;
            }

            var name = provider.ParameterName(index);
            whereParts.Add($"{provider.QuoteIdentifier(pk)} = {provider.ParameterToken(index)}");
            parameters[name] = value;
            index++;
        }
        return string.Join(" AND ", whereParts);
    }

    private static string QualifiedTable(IMetadataProvider provider, GridEditContext context)
        => string.IsNullOrEmpty(context.Database)
            ? $"{provider.QuoteIdentifier(context.Schema)}.{provider.QuoteIdentifier(context.Table)}"
            : $"{provider.QuoteIdentifier(context.Database)}.{provider.QuoteIdentifier(context.Schema)}.{provider.QuoteIdentifier(context.Table)}";
}
