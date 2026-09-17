using ClosedXML.Excel;
using Rag.DocumentProcessing.Constants;
using Rag.DocumentProcessing.Interfaces;
using System.Text;
using System.Text.RegularExpressions;

namespace Rag.DocumentProcessing.Parsers;

public class ExcelDocumentParser : IDocumentParser
{
    private static readonly Regex CellReferenceRegex =
        new(@"^[A-Z]+", RegexOptions.Compiled);

    public Task<string> ParseAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        using var workbook =
            new XLWorkbook(filePath);

        var builder =
            new StringBuilder();

        var fileName =
            Path.GetFileNameWithoutExtension(filePath);

        builder.AppendLine(
            $"Excel Document: {fileName}");

        foreach (var worksheet in workbook.Worksheets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var range =
                worksheet.RangeUsed();

            if (range == null)
            {
                continue;
            }

            var sheetText =
                ParseWorksheet(
                    worksheet,
                    range,
                    cancellationToken);

            if (string.IsNullOrWhiteSpace(
                sheetText))
            {
                continue;
            }

            builder.AppendLine();
            builder.AppendLine(
                $"Sheet: {worksheet.Name}");

            builder.AppendLine();

            builder.AppendLine(
                sheetText);
        }

        return Task.FromResult(
            NormalizeDocumentText(
                builder.ToString()));
    }

    public Task<List<string>> ParseAndChunkAsync(
        string filePath,
        int chunkSize = DocumentConstants.ChunkSize,
        int childChunkSize = DocumentConstants.ChildChunkSize,
        int overlap = DocumentConstants.Overlap,
        CancellationToken cancellationToken = default)
    {
        using var workbook =
            new XLWorkbook(filePath);

        var parentChunks =
            new List<string>();

        foreach (var worksheet in workbook.Worksheets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var range =
                worksheet.RangeUsed();

            if (range == null)
            {
                continue;
            }

            var rows =
                ExtractWorksheetRows(
                    worksheet,
                    range,
                    cancellationToken);

            if (rows.Count == 0)
            {
                continue;
            }

            var sheetBlocks =
                ConvertRowsToSemanticBlocks(
                    worksheet.Name,
                    rows);

            var chunks =
                BuildChunks(
                    sheetBlocks,
                    chunkSize);

            parentChunks.AddRange(
                chunks);
        }

        return Task.FromResult(
            parentChunks);
    }

    private static string ParseWorksheet(
        IXLWorksheet worksheet,
        IXLRange range,
        CancellationToken cancellationToken)
    {
        var rows =
            ExtractWorksheetRows(
                worksheet,
                range,
                cancellationToken);

        if (rows.Count == 0)
        {
            return string.Empty;
        }

        var blocks =
            ConvertRowsToSemanticBlocks(
                worksheet.Name,
                rows);

        return string.Join(
            Environment.NewLine +
            Environment.NewLine,
            blocks);
    }

    private static List<ExcelRow> ExtractWorksheetRows(
        IXLWorksheet worksheet,
        IXLRange range,
        CancellationToken cancellationToken)
    {
        var rows =
            new List<ExcelRow>();

        foreach (var row in range.Rows())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var cells =
                new List<string>();

            var hasValue = false;

            foreach (var cell in row.Cells())
            {
                var value =
                    GetCellText(cell);

                cells.Add(value);

                if (!string.IsNullOrWhiteSpace(
                    value))
                {
                    hasValue = true;
                }
            }

            if (!hasValue)
            {
                continue;
            }

            rows.Add(
                new ExcelRow
                {
                    RowNumber = row.RowNumber(),
                    Cells = cells
                });
        }

        return rows;
    }

    private static List<string> ConvertRowsToSemanticBlocks(
        string sheetName,
        List<ExcelRow> rows)
    {
        var blocks =
            new List<string>();

        if (rows.Count == 0)
        {
            return blocks;
        }

        var headerRow =
            DetectHeaderRow(rows);

        var headers =
            BuildHeaders(
                headerRow);

        var dataRows =
            rows
                .Where(row =>
                    row.RowNumber >
                    headerRow.RowNumber)
                .ToList();

        if (dataRows.Count == 0)
        {
            var singleBlock =
                ConvertSingleRow(
                    sheetName,
                    headers);

            if (!string.IsNullOrWhiteSpace(
                singleBlock))
            {
                blocks.Add(
                    singleBlock);
            }

            return blocks;
        }

        foreach (var row in dataRows)
        {
            var block =
                ConvertDataRow(
                    sheetName,
                    headers,
                    row);

            if (!string.IsNullOrWhiteSpace(
                block))
            {
                blocks.Add(block);
            }
        }

        return blocks;
    }

    private static ExcelRow DetectHeaderRow(
        List<ExcelRow> rows)
    {
        if (rows.Count == 1)
        {
            return rows[0];
        }

        var firstRow =
            rows[0];

        var secondRow =
            rows[1];

        if (LooksLikeHeader(
            firstRow,
            secondRow))
        {
            return firstRow;
        }

        return firstRow;
    }

    private static bool LooksLikeHeader(
        ExcelRow header,
        ExcelRow data)
    {
        var headerValues =
            header.Cells
                .Where(x =>
                    !string.IsNullOrWhiteSpace(x))
                .ToList();

        var dataValues =
            data.Cells
                .Where(x =>
                    !string.IsNullOrWhiteSpace(x))
                .ToList();

        if (headerValues.Count == 0)
        {
            return false;
        }

        if (dataValues.Count == 0)
        {
            return true;
        }

        var headerScore =
            headerValues.Count;

        var duplicateHeaders =
            headerValues
                .GroupBy(
                    x => x,
                    StringComparer.OrdinalIgnoreCase)
                .Count(group =>
                    group.Count() > 1);

        if (duplicateHeaders > 0)
        {
            headerScore -= duplicateHeaders;
        }

        return headerScore >=
               Math.Min(
                   2,
                   dataValues.Count);
    }

    private static List<string> BuildHeaders(
        ExcelRow headerRow)
    {
        var headers =
            new List<string>();

        for (var index = 0;
             index < headerRow.Cells.Count;
             index++)
        {
            var header =
                headerRow.Cells[index].Trim();

            if (string.IsNullOrWhiteSpace(
                header))
            {
                header =
                    $"Column {index + 1}";
            }

            var originalHeader =
                header;

            var duplicateNumber = 2;

            while (headers.Any(
                existing =>
                    string.Equals(
                        existing,
                        header,
                        StringComparison
                            .OrdinalIgnoreCase)))
            {
                header =
                    $"{originalHeader} {duplicateNumber}";

                duplicateNumber++;
            }

            headers.Add(header);
        }

        return headers;
    }

    private static string ConvertSingleRow(
        string sheetName,
        List<string> headers)
    {
        if (headers.Count == 0)
        {
            return string.Empty;
        }

        var builder =
            new StringBuilder();

        builder.AppendLine(
            $"Sheet: {sheetName}");

        foreach (var header in headers)
        {
            if (!string.IsNullOrWhiteSpace(
                header))
            {
                builder.AppendLine(
                    $"{header}:");
            }
        }

        return builder.ToString().Trim();
    }

    private static string ConvertDataRow(
        string sheetName,
        List<string> headers,
        ExcelRow row)
    {
        var values =
            new List<string>();

        var maxColumns =
            Math.Min(
                headers.Count,
                row.Cells.Count);

        for (var index = 0;
             index < maxColumns;
             index++)
        {
            var value =
                row.Cells[index].Trim();

            if (string.IsNullOrWhiteSpace(
                value))
            {
                continue;
            }

            var header =
                headers[index];

            if (string.IsNullOrWhiteSpace(
                header))
            {
                header =
                    $"Column {index + 1}";
            }

            values.Add(
                $"{header}: {value}");
        }

        if (values.Count == 0)
        {
            return string.Empty;
        }

        var builder =
            new StringBuilder();

        builder.AppendLine(
            $"Sheet: {sheetName}");

        builder.AppendLine(
            $"Excel Row: {row.RowNumber}");

        foreach (var value in values)
        {
            builder.AppendLine(value);
        }

        return builder.ToString().Trim();
    }

    private static List<string> BuildChunks(
        List<string> blocks,
        int chunkSize)
    {
        var chunks =
            new List<string>();

        if (blocks.Count == 0)
        {
            return chunks;
        }

        var current =
            new StringBuilder();

        foreach (var rawBlock in blocks)
        {
            if (string.IsNullOrWhiteSpace(
                rawBlock))
            {
                continue;
            }

            var block =
                NormalizeDocumentText(
                    rawBlock);

            if (string.IsNullOrWhiteSpace(
                block))
            {
                continue;
            }

            var separator =
                current.Length == 0
                    ? string.Empty
                    : Environment.NewLine +
                      Environment.NewLine;

            var candidateLength =
                current.Length +
                separator.Length +
                block.Length;

            if (candidateLength <=
                chunkSize)
            {
                current.Append(
                    separator);

                current.Append(
                    block);

                continue;
            }

            if (current.Length > 0)
            {
                chunks.Add(
                    current
                        .ToString()
                        .Trim());

                current.Clear();
            }

            if (block.Length <=
                chunkSize)
            {
                current.Append(
                    block);

                continue;
            }

            var largeBlocks =
                SplitLargeBlock(
                    block,
                    chunkSize);

            for (var index = 0;
                 index < largeBlocks.Count - 1;
                 index++)
            {
                chunks.Add(
                    largeBlocks[index]);
            }

            if (largeBlocks.Count > 0)
            {
                current.Append(
                    largeBlocks[^1]);
            }
        }

        if (current.Length > 0)
        {
            chunks.Add(
                current
                    .ToString()
                    .Trim());
        }

        return chunks;
    }

    private static List<string> SplitLargeBlock(
        string text,
        int chunkSize)
    {
        var result =
            new List<string>();

        var lines =
            text.Split(
                new[]
                {
                    "\r\n",
                    "\n",
                    "\r"
                },
                StringSplitOptions.RemoveEmptyEntries);

        var current =
            new StringBuilder();

        foreach (var line in lines)
        {
            var cleanLine =
                line.Trim();

            if (string.IsNullOrWhiteSpace(
                cleanLine))
            {
                continue;
            }

            var separator =
                current.Length == 0
                    ? string.Empty
                    : Environment.NewLine;

            if (current.Length +
                separator.Length +
                cleanLine.Length <=
                chunkSize)
            {
                current.Append(
                    separator);

                current.Append(
                    cleanLine);

                continue;
            }

            if (current.Length > 0)
            {
                result.Add(
                    current
                        .ToString()
                        .Trim());

                current.Clear();
            }

            if (cleanLine.Length <=
                chunkSize)
            {
                current.Append(
                    cleanLine);

                continue;
            }

            SplitLongLine(
                cleanLine,
                chunkSize,
                result,
                current);
        }

        if (current.Length > 0)
        {
            result.Add(
                current
                    .ToString()
                    .Trim());
        }

        return result;
    }

    private static void SplitLongLine(
        string text,
        int chunkSize,
        List<string> result,
        StringBuilder current)
    {
        var words =
            text.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries);

        foreach (var word in words)
        {
            var separator =
                current.Length == 0
                    ? string.Empty
                    : " ";

            if (current.Length +
                separator.Length +
                word.Length <=
                chunkSize)
            {
                current.Append(
                    separator);

                current.Append(
                    word);

                continue;
            }

            if (current.Length > 0)
            {
                result.Add(
                    current
                        .ToString()
                        .Trim());

                current.Clear();
            }

            current.Append(word);
        }
    }

    private static string GetCellText(
        IXLCell cell)
    {
        if (cell.IsEmpty())
        {
            return string.Empty;
        }

        try
        {
            var value =
                cell.GetFormattedString();

            if (!string.IsNullOrWhiteSpace(
                value))
            {
                return NormalizeCellText(
                    value);
            }
        }
        catch
        {
        }

        return NormalizeCellText(
            cell.GetString());
    }

    private static string NormalizeCellText(
        string value)
    {
        if (string.IsNullOrWhiteSpace(
            value))
        {
            return string.Empty;
        }

        return string.Join(
            " ",
            value
                .Replace("\r\n", " ")
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Split(
                    ' ',
                    StringSplitOptions
                        .RemoveEmptyEntries))
            .Trim();
    }

    private static string NormalizeDocumentText(
        string text)
    {
        if (string.IsNullOrWhiteSpace(
            text))
        {
            return string.Empty;
        }

        text = text
            .Replace("\r\n", "\n")
            .Replace("\r", "\n");

        var lines =
            text
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line =>
                    !string.IsNullOrWhiteSpace(
                        line));

        return string.Join(
            Environment.NewLine,
            lines);
    }

    private sealed class ExcelRow
    {
        public int RowNumber { get; init; }

        public List<string> Cells { get; init; } =
            new();
    }
}