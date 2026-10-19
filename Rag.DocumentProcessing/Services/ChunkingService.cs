using DocumentFormat.OpenXml.Drawing.Charts;
using Rag.DocumentProcessing.Constants;
using Rag.DocumentProcessing.Interfaces;
using Rag.DocumentProcessing.Models;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace Rag.DocumentProcessing.Services;

public class ChunkingService : IChunkingService
{
    public IReadOnlyList<DocumentChunk> CreateChunks(
        DocumentInfo documentInfo,
        string content,
        int maxChunkSize)
    {
        var chunks = new List<DocumentChunk>();

        if (string.IsNullOrWhiteSpace(content) || maxChunkSize <= 0)
            return chunks;

        var words = Regex.Matches(content, @"\S+")
            .Cast<Match>()
            .Select(match => match.Value)
            .ToList();

        if (words.Count == 0)
            return chunks;

        var currentWords = new List<string>();
        var chunkNumber = 1;

        foreach (var word in words)
        {
            if (currentWords.Count > 0 && currentWords.Count + 1 > maxChunkSize)
            {
                chunks.Add(CreateNewChunk(documentInfo, chunkNumber++, string.Join(" ", currentWords), string.Empty));
                currentWords.Clear();
            }

            currentWords.Add(word);
        }

        if (currentWords.Count > 0)
        {
            chunks.Add(CreateNewChunk(documentInfo, chunkNumber, string.Join(" ", currentWords), string.Empty));
        }

        return chunks;
    }

    private static DocumentChunk CreateNewChunk(DocumentInfo doc, int chunkNumber, string text, string parentText, string tableId = "")
    {
        return new DocumentChunk
        {
            DocumentId = doc.DocumentId.ToString(),
            DocumentName = doc.DocumentName,
            DocumentType = doc.DocumentType,
            RelativePath = doc.RelativePath,
            ChunkNumber = chunkNumber,
            Text = text.Trim(),
            ParentText = parentText.Trim(),
            TableId = tableId,
            ParentBlockId = BuildParentBlockId(doc, parentText)
        };
    }

    public IReadOnlyList<DocumentChunk> CreateChildChunks(
       DocumentInfo documentInfo,
       List<string> content,
       int childChunkSize = 20, //DocumentConstants.ChildChunkSize,
       int overlap = 10 //DocumentConstants.Overlap
                        )
    {
        var chunks = new List<DocumentChunk>();
        var globalChunkNumber = 1;

        var parentIndex = 0;
        var precedingBlock = string.Empty;
        foreach (var parentText in content)
        {
            parentIndex++;
            if (string.IsNullOrWhiteSpace(parentText) || childChunkSize <= 0)
                continue;

            if (IsMarkdownTable(parentText))
            {
                var tableContext = ExtractTableContext(precedingBlock);
                AddTableChildChunks(
                    chunks,
                    documentInfo,
                    parentText,
                    childChunkSize,
                    overlap,
                    BuildTableId(documentInfo, parentText, tableContext),
                    tableContext,
                    ref globalChunkNumber);
                continue;
            }

            var words = Regex.Matches(parentText, @"\S+")
                .Cast<Match>()
                .Select(match => match.Value)
                .ToList();

            if (words.Count == 0)
                continue;

            precedingBlock = parentText;

            var currentIndex = 0;

            while (currentIndex < words.Count)
            {
                var length = Math.Min(childChunkSize, words.Count - currentIndex);
                var childWords = words.Skip(currentIndex).Take(length).ToList();
                var childText = string.Join(" ", childWords).Trim();

                if (!string.IsNullOrEmpty(childText))
                {
                    chunks.Add(CreateNewChunk(documentInfo, globalChunkNumber, childText, parentText));
                    globalChunkNumber++;
                }

                if (currentIndex + childChunkSize >= words.Count)
                {
                    break;
                }

                currentIndex += Math.Max(1, childChunkSize - overlap);
            }
        }

        return chunks;
    }

    public IReadOnlyList<DocumentChunk> CreateExcelTableChunks(
        DocumentInfo documentInfo,
        IReadOnlyList<ExcelTableBlock> tables,
        int maxChunkWords = DocumentConstants.ChildChunkSize)
    {
        if (maxChunkWords <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxChunkWords));

        var chunks = new List<DocumentChunk>();
        var chunkNumber = 1;

        foreach (var table in tables)
        {
            if (table.Headers.Count == 0 || table.Rows.Count == 0)
                continue;

            var tableId = BuildExcelTableId(documentInfo, table);
            var tableHeader = FormatExcelRow(table.Headers);
            var sectionPrefix = string.IsNullOrWhiteSpace(table.TableContext)
                ? string.Empty
                : $"[Section: {table.TableContext}]";
            var fixedWordCount =
                table.Headers.Sum(CountWords) +
                CountWords(sectionPrefix);
            var rowWordCapacity = maxChunkWords - fixedWordCount;

            if (rowWordCapacity <= 0)
            {
                throw new InvalidOperationException(
                    $"The repeated header for Excel table '{table.SheetName}' " +
                    $"uses {fixedWordCount} words, exceeding the {maxChunkWords}-word chunk limit.");
            }

            var parentText = string.Join(
                Environment.NewLine,
                table.TableContext,
                tableHeader);

            var chunkRows = new List<ExcelTableRow>();
            var chunkWordCount = fixedWordCount;

            void FlushChunk()
            {
                if (chunkRows.Count == 0)
                    return;

                var textBuilder = new StringBuilder();

                if (!string.IsNullOrWhiteSpace(sectionPrefix))
                    textBuilder.AppendLine(sectionPrefix);

                textBuilder.AppendLine(tableHeader);
                foreach (var row in chunkRows)
                    textBuilder.AppendLine(FormatExcelRow(row.Cells));

                var rows = chunkRows.ToList();
                chunks.Add(new DocumentChunk
                {
                    DocumentId = documentInfo.DocumentId.ToString(),
                    DocumentName = documentInfo.DocumentName,
                    DocumentType = documentInfo.DocumentType,
                    RelativePath = documentInfo.RelativePath,
                    ChunkNumber = chunkNumber++,
                    Text = textBuilder.ToString().Trim(),
                    ParentText = parentText,
                    TableId = tableId,
                    ParentBlockId = tableId,
                    TableContext = table.TableContext,
                    SheetName = table.SheetName,
                    RowStartIndex = rows[0].RowIndex,
                    RowEndIndex = rows[^1].RowIndex,
                    RowIdentifiers = rows
                        .SelectMany(row => row.Identifiers)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    StructuredRowsJson = JsonSerializer.Serialize(
                        rows.Select(row => new
                        {
                            rowIndex = row.RowIndex,
                            headerText = tableHeader,
                            rowText = FormatExcelRow(row.Cells),
                            fields = row.Fields
                        }))
                });

                chunkRows.Clear();
                chunkWordCount = fixedWordCount;
            }

            foreach (var sourceRow in table.Rows)
            {
                foreach (var row in SplitExcelRow(sourceRow, table.Headers.Count, rowWordCapacity))
                {
                    var rowWordCount = row.Cells.Sum(CountWords);

                    if (chunkRows.Count > 0 &&
                        chunkWordCount + rowWordCount > maxChunkWords)
                    {
                        FlushChunk();
                    }

                    chunkRows.Add(row);
                    chunkWordCount += rowWordCount;
                }
            }

            FlushChunk();
        }

        return chunks;
    }

    private static IReadOnlyList<ExcelTableRow> SplitExcelRow(
        ExcelTableRow row,
        int columnCount,
        int maxRowWords)
    {
        if (row.Cells.Sum(CountWords) <= maxRowWords)
            return [row];

        if (row.Cells.Count == 0)
        {
            throw new InvalidOperationException(
                $"Excel row {row.RowIndex} exceeds the chunk word limit and has no cell values for safe splitting.");
        }

        var cellWords = row.Cells
            .Select(cell => Regex.Matches(cell, @"\S+")
                .Cast<Match>()
                .Select(match => match.Value)
                .ToArray())
            .ToArray();
        var offsets = new int[cellWords.Length];
        var fragments = new List<ExcelTableRow>();

        while (offsets.Where((offset, index) => offset < cellWords[index].Length).Any())
        {
            var remainingWords = maxRowWords;
            var fragmentCells = Enumerable
                .Repeat(string.Empty, columnCount)
                .ToArray();

            for (var cellIndex = 0; cellIndex < columnCount; cellIndex++)
            {
                if (remainingWords == 0)
                    break;

                if (cellIndex < cellWords.Length &&
                    offsets[cellIndex] < cellWords[cellIndex].Length)
                {
                    var takeCount = Math.Min(
                        remainingWords,
                        cellWords[cellIndex].Length - offsets[cellIndex]);
                    fragmentCells[cellIndex] = string.Join(
                        " ",
                        cellWords[cellIndex].Skip(offsets[cellIndex]).Take(takeCount));
                    offsets[cellIndex] += takeCount;
                    remainingWords -= takeCount;
                }
                else
                {
                    fragmentCells[cellIndex] = string.Empty;
                }
            }

            fragments.Add(new ExcelTableRow
            {
                RowIndex = row.RowIndex,
                Cells = fragmentCells,
                Text = FormatExcelRow(fragmentCells),
                Fields = row.Fields,
                Identifiers = row.Identifiers
            });
        }

        return fragments;
    }

    private static string BuildExcelTableId(
        DocumentInfo documentInfo,
        ExcelTableBlock table)
    {
        var identity = string.Join(
            "\n",
            documentInfo.DocumentId,
            table.SheetName,
            table.TableIndex,
            string.Join("|", table.Headers));
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..12];

        return $"{documentInfo.DocumentId}:excel-table:{hash}";
    }

    private static string FormatMarkdownRow(IReadOnlyList<string> cells)
    {
        return "| " +
               string.Join(
                   " | ",
                   cells.Select(value => value.Replace("|", "\\|", StringComparison.Ordinal))) +
               " |";
    }

    private static string FormatExcelRow(IReadOnlyList<string> cells)
    {
        return string.Join(
            '\t',
            cells.Select(value => value.Replace('\t', ' ')));
    }

    private static string FormatMarkdownSeparator(int columnCount)
    {
        return "| " + string.Join(" | ", Enumerable.Repeat("---", columnCount)) + " |";
    }

    private static void AddTableChildChunks(
        List<DocumentChunk> chunks,
        DocumentInfo documentInfo,
        string tableText,
        int childChunkSize,
        int overlap,
        string tableId,
        string tableContext,
        ref int globalChunkNumber)
    {
        var lines = tableText
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        if (lines.Count < 2)
        {
            AddWordChunks(
                chunks,
                documentInfo,
                tableText,
                tableText,
                childChunkSize,
                overlap,
                ref globalChunkNumber);
            return;
        }

        var header = string.Join(Environment.NewLine, lines.Take(2));
        var dataRows = lines.Skip(2).ToList();
        var headerWordCount = CountWords(header);
        var dataCapacity = Math.Max(1, childChunkSize - headerWordCount);
        var start = 0;

        while (start < dataRows.Count)
        {
            var selectedRows = new List<string>();
            var selectedWordCount = 0;

            for (var index = start;
                 index < dataRows.Count;
                 index++)
            {
                var rowWordCount = CountWords(dataRows[index]);
                if (selectedRows.Count > 0 &&
                    selectedWordCount + rowWordCount > dataCapacity)
                {
                    break;
                }

                selectedRows.Add(dataRows[index]);
                selectedWordCount += rowWordCount;
            }

            var chunk = CreateNewChunk(
                documentInfo,
                globalChunkNumber++,
                string.Join(
                    Environment.NewLine,
                    new[]
                    {
                        string.IsNullOrWhiteSpace(tableContext)
                            ? header
                            : $"[Section: {tableContext}]{Environment.NewLine}{header}"
                    }.Concat(selectedRows)),
                tableText,
                tableId);
            chunk.TableContext = tableContext;
            chunks.Add(chunk);

            if (start + selectedRows.Count >= dataRows.Count)
                break;

            var overlapWordCount = 0;
            var nextStart = start + selectedRows.Count;
            for (var index = selectedRows.Count - 1;
                 index >= 0;
                 index--)
            {
                var rowWordCount = CountWords(selectedRows[index]);
                if (overlapWordCount + rowWordCount > overlap)
                    break;

                overlapWordCount += rowWordCount;
                nextStart--;
            }

            start = Math.Max(start + 1, nextStart);
        }
    }

    private static int CountWords(string text)
    {
        return string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static void AddWordChunks(
        List<DocumentChunk> chunks,
        DocumentInfo documentInfo,
        string text,
        string parentText,
        int childChunkSize,
        int overlap,
        ref int globalChunkNumber)
    {
        var words = Regex.Matches(text, @"\S+")
            .Cast<Match>()
            .Select(match => match.Value)
            .ToList();
        var step = Math.Max(1, childChunkSize - overlap);

        for (var start = 0; start < words.Count; start += step)
        {
            var childText = string.Join(
                " ",
                words.Skip(start).Take(childChunkSize));

            if (!string.IsNullOrWhiteSpace(childText))
            {
                chunks.Add(CreateNewChunk(
                    documentInfo,
                    globalChunkNumber++,
                    childText,
                    parentText));
            }

            if (start + childChunkSize >= words.Count)
                break;
        }
    }

    private static bool IsMarkdownTable(string text)
    {
        var lines = text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .ToList();

        return lines.Count >= 2 &&
               lines[0].Contains('|') &&
               lines[1].Contains('|') &&
               lines[1].Replace("|", string.Empty)
                   .Replace("-", string.Empty)
                   .Replace(":", string.Empty)
                   .Trim()
                   .Length == 0;
    }

    private static string BuildTableId(
        DocumentInfo documentInfo,
        string tableText,
        string tableContext)
    {
        var lines = tableText
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Take(2);
        var normalized = Regex.Replace(
            $"{tableContext}\n{string.Join("\n", lines)}",
            @"\s+",
            " ")
            .Trim();
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..12];

        return $"{documentInfo.DocumentId}:table:{hash}";
    }

    private static string BuildParentBlockId(
        DocumentInfo documentInfo,
        string parentText)
    {
        if (string.IsNullOrWhiteSpace(parentText))
            return string.Empty;

        var normalized = Regex.Replace(parentText, @"\s+", " ").Trim();
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..12];

        return $"{documentInfo.DocumentId}:parent:{hash}";
    }

    private static string ExtractTableContext(string precedingBlock)
    {
        if (string.IsNullOrWhiteSpace(precedingBlock))
            return string.Empty;

        var candidate = precedingBlock
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .LastOrDefault();

        if (candidate is null)
            return string.Empty;

        var lastCell = candidate
            .Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(cell => cell.Trim())
            .LastOrDefault() ?? candidate;

        if (lastCell.Length <= 80 &&
            CountWords(lastCell) <= 8 &&
            !IsMarkdownTable(lastCell))
        {
            return lastCell;
        }

        return string.Empty;
    }
}