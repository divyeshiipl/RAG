using ClosedXML.Excel;
using Rag.DocumentProcessing.Constants;
using Rag.DocumentProcessing.Interfaces;
using Rag.DocumentProcessing.Models;
using System.Text;

namespace Rag.DocumentProcessing.Parsers;

public class ExcelDocumentParser : IDocumentParser, IExcelTableParser
{
    public Task<string> ParseAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        using var workbook = new XLWorkbook(filePath);

        var builder = new StringBuilder();

        var fileName = Path.GetFileNameWithoutExtension(filePath);

        builder.AppendLine($"Excel Document: {fileName}");

        foreach (var worksheet in workbook.Worksheets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var range = worksheet.RangeUsed();

            if (range == null)
                continue;

            var sheetText = ConvertSheetToMarkdown(
                worksheet,
                range,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(sheetText))
                continue;

            builder.AppendLine();
            builder.AppendLine($"Sheet: {worksheet.Name}");
            builder.AppendLine();
            builder.AppendLine(sheetText);
        }

        return Task.FromResult(
            NormalizeDocumentText(builder.ToString()));
    }

    public async Task<List<string>> ParseAndChunkAsync(
        string filePath,
        int chunkSize = DocumentConstants.ChunkSize,
        int childChunkSize = DocumentConstants.ChildChunkSize,
        int overlap = DocumentConstants.Overlap,
        CancellationToken cancellationToken = default)
    {
        var tables = await ParseTablesAsync(filePath, cancellationToken);
        return tables.Select(table =>
        {
            var lines = new List<string>();
            lines.Add(FormatMarkdownRow(table.Headers));
            lines.Add(FormatMarkdownSeparator(table.Headers.Count));
            lines.AddRange(table.Rows.Select(row => row.Text));
            return string.Join(Environment.NewLine, lines);
        }).ToList();
    }

    public Task<IReadOnlyList<ExcelTableBlock>> ParseTablesAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        using var workbook = new XLWorkbook(filePath);
        var tables = new List<ExcelTableBlock>();

        foreach (var worksheet in workbook.Worksheets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var range = worksheet.RangeUsed();
            if (range is null)
                continue;

            var currentRows = new List<ExcelSourceRow>();
            var tableContext = string.Empty;
            var tableIndex = 0;

            void FlushTable()
            {
                if (currentRows.Count >= 2)
                {
                    var header = currentRows[0];
                    var dataRows = currentRows.Skip(1).ToList();
                    var rowKeyColumns = GetUniqueValueColumns(header.Cells.Count, dataRows);

                    if (header.Cells.Count > 0 && dataRows.Count > 0)
                    {
                        tables.Add(new ExcelTableBlock
                        {
                            SheetName = worksheet.Name,
                            TableContext = tableContext,
                            TableIndex = ++tableIndex,
                            Headers = header.Cells,
                            Rows = dataRows.Select(row => new ExcelTableRow
                            {
                                RowIndex = row.RowIndex,
                                Text = FormatMarkdownRow(row.Cells, header.Cells.Count),
                                Cells = row.Cells,
                                Fields = CreateStructuredFields(header.Cells, row.Cells),
                                Identifiers = rowKeyColumns
                                    .Where(index => index < row.Cells.Count)
                                    .Select(index => row.Cells[index])
                                    .Where(value => !string.IsNullOrWhiteSpace(value))
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .ToList()
                            }).ToList()
                        });
                    }
                }

                currentRows.Clear();
            }

            foreach (var row in range.Rows())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var cells = row.Cells()
                    .Select(GetCellText)
                    .ToList();

                while (cells.Count > 0 && string.IsNullOrWhiteSpace(cells[^1]))
                    cells.RemoveAt(cells.Count - 1);

                if (cells.All(string.IsNullOrWhiteSpace))
                {
                    FlushTable();
                    tableContext = string.Empty;
                    continue;
                }

                var populatedCells = cells.Count(value => !string.IsNullOrWhiteSpace(value));
                if (populatedCells == 1 && currentRows.Count > 0)
                {
                    FlushTable();
                    tableContext = cells.First(value => !string.IsNullOrWhiteSpace(value));
                    continue;
                }

                if (currentRows.Count >= 2 &&
                    RowsMatch(currentRows[0].Cells, cells))
                {
                    FlushTable();
                }

                if (currentRows.Count == 0 && populatedCells == 1)
                {
                    tableContext = cells.First(value => !string.IsNullOrWhiteSpace(value));
                    continue;
                }

                currentRows.Add(new ExcelSourceRow(row.RowNumber(), cells));
            }

            FlushTable();
        }

        return Task.FromResult<IReadOnlyList<ExcelTableBlock>>(tables);
    }

    private static List<int> GetUniqueValueColumns(
        int columnCount,
        IReadOnlyList<ExcelSourceRow> rows)
    {
        return Enumerable.Range(0, columnCount)
            .Where(columnIndex =>
            {
                var values = rows
                    .Select(row => columnIndex < row.Cells.Count
                        ? row.Cells[columnIndex].Trim()
                        : string.Empty)
                    .ToList();
                var nonEmptyValues = values
                    .Where(value => value.Length > 0)
                    .ToList();

                return nonEmptyValues.Count == rows.Count &&
                       nonEmptyValues.Distinct(StringComparer.OrdinalIgnoreCase).Count() == rows.Count;
            })
            .ToList();
    }

    private static IReadOnlyDictionary<string, string> CreateStructuredFields(
        IReadOnlyList<string> headers,
        IReadOnlyList<string> cells)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < headers.Count; index++)
        {
            var header = string.IsNullOrWhiteSpace(headers[index])
                ? $"Column {index + 1}"
                : headers[index].Trim();

            var uniqueHeader = header;
            var duplicateIndex = 2;
            while (fields.ContainsKey(uniqueHeader))
                uniqueHeader = $"{header} ({duplicateIndex++})";

            fields[uniqueHeader] = index < cells.Count ? cells[index] : string.Empty;
        }

        return fields;
    }

    private static bool RowsMatch(
        IReadOnlyList<string> first,
        IReadOnlyList<string> candidate)
    {
        return first.Count == candidate.Count &&
               first.SequenceEqual(candidate, StringComparer.OrdinalIgnoreCase);
    }

    private static string FormatMarkdownRow(
        IReadOnlyList<string> cells,
        int columnCount)
    {
        return "| " +
               string.Join(
                   " | ",
                   Enumerable.Range(0, columnCount)
                       .Select(index => index < cells.Count ? cells[index] : string.Empty)
                       .Select(value => value.Replace("|", "\\|", StringComparison.Ordinal))) +
               " |";
    }

    private static string FormatMarkdownRow(IReadOnlyList<string> cells)
    {
        return FormatMarkdownRow(cells, cells.Count);
    }

    private static string FormatMarkdownSeparator(int columnCount)
    {
        return "| " + string.Join(" | ", Enumerable.Repeat("---", columnCount)) + " |";
    }

    private sealed record ExcelSourceRow(int RowIndex, List<string> Cells);

    private static string ConvertSheetToMarkdown(
        IXLWorksheet worksheet,
        IXLRange range,
        CancellationToken cancellationToken)
    {
        var rows = new List<List<string>>();

        foreach (var row in range.Rows())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var cells = new List<string>();
            var hasValue = false;

            foreach (var cell in row.Cells())
            {
                var value = GetCellText(cell);
                cells.Add(value);

                if (!string.IsNullOrWhiteSpace(value))
                    hasValue = true;
            }

            if (!hasValue)
                continue;

            rows.Add(cells);
        }

        if (rows.Count == 0)
            return string.Empty;

        var columnCount = rows.Max(r => r.Count);

        var builder = new StringBuilder();

        foreach (var row in rows)
        {
            builder.Append("| ");

            for (var i = 0; i < columnCount; i++)
            {
                var value = i < row.Count ? row[i] : string.Empty;

                // Escape pipe characters
                value = value.Replace("|", "\\|");

                builder.Append(value);
                builder.Append(" | ");
            }

            builder.AppendLine();
        }

        return builder.ToString().Trim();
    }

    private static string GetCellText(IXLCell cell)
    {
        if (cell.IsEmpty())
            return string.Empty;

        try
        {
            var value = cell.GetFormattedString();

            if (!string.IsNullOrWhiteSpace(value))
                return NormalizeCellText(value);
        }
        catch
        {
        }

        return NormalizeCellText(cell.GetString());
    }

    private static string NormalizeCellText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return string.Join(
            " ",
            value
                .Replace("\r\n", " ")
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Trim();
    }

    private static string NormalizeDocumentText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        text = text
            .Replace("\r\n", "\n")
            .Replace("\r", "\n");

        var lines = text
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line));

        return string.Join(Environment.NewLine, lines);
    }
}