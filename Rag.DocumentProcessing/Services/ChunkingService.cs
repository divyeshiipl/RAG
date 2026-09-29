using DocumentFormat.OpenXml.Drawing.Charts;
using Rag.DocumentProcessing.Constants;
using Rag.DocumentProcessing.Interfaces;
using Rag.DocumentProcessing.Models;
using System.Text;
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
        return Regex.Matches(text, @"\S+").Count;
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

        var numberedHeading = Regex.Match(
            lastCell,
            @"^\d+(?:\.\d+)*\.?\s+\S+(?:\s+\S+){0,2}");
        if (numberedHeading.Success)
            return numberedHeading.Value;

        if (lastCell.Length <= 80 &&
            CountWords(lastCell) <= 8 &&
            !IsMarkdownTable(lastCell))
        {
            return lastCell;
        }

        return string.Empty;
    }
}