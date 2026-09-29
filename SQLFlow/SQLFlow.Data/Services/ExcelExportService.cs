using ClosedXML.Excel;
using SQLFlow.Data.Models;

namespace SQLFlow.Data.Services;

public static class ExcelExportService
{
    public static byte[] Export(QueryResult result)
        => Export(new[] { ("Resultado", result) });

    /// <summary>Um workbook só, uma aba por resultado — usado quando várias consultas rodaram ao mesmo
    /// tempo (ver QueryTabModel.ParallelResults) e cada uma precisa da sua própria planilha em vez de
    /// misturar linhas de consultas diferentes numa aba só.</summary>
    public static byte[] Export(IReadOnlyList<(string SheetName, QueryResult Result)> results)
    {
        using var workbook = new XLWorkbook();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (sheetName, result) in results)
        {
            var sheet = workbook.Worksheets.Add(UniqueSheetName(sheetName, usedNames));

            for (var c = 0; c < result.Columns.Count; c++)
            {
                var header = sheet.Cell(1, c + 1);
                header.Value = result.Columns[c];
                header.Style.Font.Bold = true;
                header.Style.Fill.BackgroundColor = XLColor.FromHtml("#EEF1F4");
            }

            for (var r = 0; r < result.Rows.Count; r++)
            {
                var row = result.Rows[r];
                for (var c = 0; c < result.Columns.Count; c++)
                {
                    SetCellValue(sheet.Cell(r + 2, c + 1), row[result.Columns[c]]);
                }
            }

            sheet.SheetView.FreezeRows(1);
            sheet.Columns().AdjustToContents();
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    /// <summary>Nome de aba do Excel: no máximo 31 caracteres, sem : \ / ? * [ ], e sem repetir um nome
    /// já usado no mesmo workbook (ex: duas consultas com o mesmo texto truncado).</summary>
    private static string UniqueSheetName(string desired, HashSet<string> usedNames)
    {
        var sanitized = new string(desired.Select(ch => "\\/?*[]:".Contains(ch) ? '_' : ch).ToArray()).Trim();
        if (sanitized.Length == 0) sanitized = "Consulta";
        if (sanitized.Length > 31) sanitized = sanitized[..31];

        var candidate = sanitized;
        var suffix = 2;
        while (!usedNames.Add(candidate))
        {
            var suffixText = $" ({suffix})";
            candidate = sanitized[..Math.Min(sanitized.Length, 31 - suffixText.Length)] + suffixText;
            suffix++;
        }

        return candidate;
    }

    private static void SetCellValue(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null:
                cell.Clear();
                break;
            case DateTime dateTime:
                cell.Value = dateTime;
                break;
            case bool boolean:
                cell.Value = boolean;
                break;
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                cell.Value = Convert.ToInt64(value);
                break;
            case float or double or decimal:
                cell.Value = Convert.ToDouble(value);
                break;
            default:
                cell.Value = value.ToString();
                break;
        }
    }
}
