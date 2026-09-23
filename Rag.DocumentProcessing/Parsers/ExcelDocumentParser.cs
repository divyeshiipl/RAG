using ClosedXML.Excel;
using Rag.DocumentProcessing.Constants;
using Rag.DocumentProcessing.Interfaces;
using System.Text;

namespace Rag.DocumentProcessing.Parsers;

public class ExcelDocumentParser : IDocumentParser
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

    public Task<List<string>> ParseAndChunkAsync(
        string filePath,
        int chunkSize = DocumentConstants.ChunkSize,
        int childChunkSize = DocumentConstants.ChildChunkSize,
        int overlap = DocumentConstants.Overlap,
        CancellationToken cancellationToken = default)
    {
        using var workbook = new XLWorkbook(filePath);

        var parentChunks = new List<string>();

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

            var chunks = BuildChunksWithHeader(
                worksheet.Name,
                sheetText,
                chunkSize);

            parentChunks.AddRange(chunks);
        }

        return Task.FromResult(parentChunks);
    }

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

    private static List<string> BuildChunksWithHeader(
     string sheetName,
     string sheetText,
     int chunkSize,
     double overlapPercent = 0.20)
    {
        var chunks = new List<string>();
        var lines = sheetText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        if (lines.Count == 0)
            return chunks;

        var headerLine = lines[0];
        var headerPrefix = $"Sheet: {sheetName}{Environment.NewLine}{headerLine}{Environment.NewLine}";
        var overlapSize = (int)(chunkSize * overlapPercent);

        var current = new List<string>();
        var currentLength = 0;

        for (var i = 1; i < lines.Count; i++)
        {
            var line = lines[i];

            if (currentLength + line.Length > chunkSize && current.Count > 0)
            {
                chunks.Add(headerPrefix + string.Join(Environment.NewLine, current));

                // Overlap: last kuch lines rakho
                var overlapLines = new List<string>();
                var overlapLength = 0;

                for (int j = current.Count - 1; j >= 0; j--)
                {
                    if (overlapLength + current[j].Length > overlapSize)
                        break;

                    overlapLines.Insert(0, current[j]);
                    overlapLength += current[j].Length;
                }

                current = overlapLines;
                currentLength = overlapLength;
            }

            current.Add(line);
            currentLength += line.Length;
        }

        if (current.Count > 0)
            chunks.Add(headerPrefix + string.Join(Environment.NewLine, current));

        return chunks;
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