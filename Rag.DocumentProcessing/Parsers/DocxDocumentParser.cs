using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Rag.DocumentProcessing.Constants;
using Rag.DocumentProcessing.Interfaces;
using Rag.DocumentProcessing.Models;
using System.Text;

namespace Rag.DocumentProcessing.Parsers;

public class DocxDocumentParser : IDocumentParser
{
    public Task<string> ParseAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        using var document = WordprocessingDocument.Open(filePath, false);
        var body = document.MainDocumentPart?.Document.Body;

        if (body is null)
            return Task.FromResult(string.Empty);

        var builder = new StringBuilder();

        foreach (var element in body.Elements())
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (element)
            {
                case Paragraph paragraph:
                    {
                        var text = GetParagraphText(paragraph);
                        if (!string.IsNullOrWhiteSpace(text))
                            builder.AppendLine(text);
                        break;
                    }

                case Table table:
                    {
                        var tableText = GetTableAsMarkdown(table);
                        if (!string.IsNullOrWhiteSpace(tableText))
                        {
                            builder.AppendLine();
                            builder.AppendLine(tableText);
                            builder.AppendLine();
                        }
                        break;
                    }
            }
        }

        return Task.FromResult(NormalizeDocumentText(builder.ToString()));
    }

    public Task<List<string>> ParseAndChunkAsync(
        string filePath,
        int chunkSize = DocumentConstants.ChunkSize,
        int childChunkSize = DocumentConstants.ChildChunkSize,
        int overlap = DocumentConstants.Overlap,
        CancellationToken cancellationToken = default)
    {
        using var document = WordprocessingDocument.Open(filePath, false);
        var body = document.MainDocumentPart?.Document.Body;

        if (body is null)
            return Task.FromResult(new List<string>());

        // Step 1: Collect blocks
        var blocks = new List<DocumentBlock>();

        foreach (var element in body.Elements())
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            switch (element)
            {
                case Paragraph paragraph:
                    {
                        var text = GetParagraphText(paragraph);
                        if (!string.IsNullOrWhiteSpace(text))
                            blocks.Add(new DocumentBlock { BlockContent = text, BlockType = "Paragraph" });
                        break;
                    }

                case Table table:
                    {
                        var tableText = GetTableAsMarkdown(table);
                        if (!string.IsNullOrWhiteSpace(tableText))
                            blocks.Add(new DocumentBlock { BlockContent = tableText, BlockType = "Table" });
                        break;
                    }
            }
        }

        // Step 2: Word-based chunking with 20% overlap
        var chunks = BuildWordBasedChunks(blocks, chunkSize, overlapPercent: 0.20);

        return Task.FromResult(chunks);
    }

    // Paragraph text — no space on Break, but line joins
    private static string GetParagraphText(Paragraph paragraph)
    {
        var builder = new StringBuilder();

        var numberingProperties = paragraph.ParagraphProperties?.NumberingProperties;
        var numberingId = numberingProperties?.NumberingId?.Val?.Value;
        var level = numberingProperties?.NumberingLevelReference?.Val?.Value ?? 0;

        foreach (var child in paragraph.ChildElements)
        {
            switch (child)
            {
                case Run run:
                    AppendRunText(builder, run);
                    break;

                case Hyperlink hyperlink:
                    AppendElementText(builder, hyperlink);
                    break;

                case SimpleField field:
                    AppendElementText(builder, field);
                    break;

                case SdtRun sdtRun:
                    AppendElementText(builder, sdtRun);
                    break;

                case BookmarkStart:
                case BookmarkEnd:
                    break;

                default:
                    if (!string.IsNullOrWhiteSpace(child.InnerText))
                        builder.Append(child.InnerText);
                    break;
            }
        }

        var text = NormalizeInlineText(builder.ToString());

        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        if (numberingId.HasValue)
        {
            var indentation = new string(' ', level * 2);
            return $"{indentation}- {text}";
        }

        return text;
    }

    //Run text — Break/Tab/CarriageReturn → SPACE
    private static void AppendRunText(StringBuilder builder, Run run)
    {
        foreach (var child in run.ChildElements)
        {
            switch (child)
            {
                case Text text:
                    builder.Append(text.Text);
                    break;

                case TabChar:
                    builder.Append(' ');
                    break;

                case Break:
                    builder.Append(' ');
                    break;

                case CarriageReturn:
                    builder.Append(' ');
                    break;

                case NoBreakHyphen:
                    builder.Append('-');
                    break;

                case SoftHyphen:
                    builder.Append('-');
                    break;

                case SymbolChar symbol:
                    if (symbol.Char is not null)
                        builder.Append(symbol.Char.Value);
                    break;
            }
        }
    }

    private static void AppendElementText(StringBuilder builder, OpenXmlElement element)
    {
        foreach (var child in element.Descendants())
        {
            switch (child)
            {
                case Text text:
                    builder.Append(text.Text);
                    break;

                case TabChar:
                    builder.Append(' ');
                    break;

                case Break:
                case CarriageReturn:
                    builder.Append(' ');
                    break;
            }
        }
    }

    // TABLE → Markdown
    private static string GetTableAsMarkdown(Table table)
    {
        var rows = table
            .Elements<TableRow>()
            .Select(row =>
                row.Elements<TableCell>()
                    .Select(GetCellText)
                    .ToList())
            .Where(row => row.Count > 0 && row.Any(c => !string.IsNullOrWhiteSpace(c)))
            .ToList();

        if (rows.Count == 0)
            return string.Empty;

        var columnCount = rows.Max(r => r.Count);
        var builder = new StringBuilder();

        var header = rows[0];
        builder.Append("| ");
        for (int i = 0; i < columnCount; i++)
        {
            var cell = i < header.Count ? header[i] : string.Empty;
            builder.Append(EscapeMarkdownCell(cell));
            builder.Append(" | ");
        }
        builder.AppendLine();

        builder.Append("| ");
        for (int i = 0; i < columnCount; i++)
        {
            builder.Append("---");
            builder.Append(" | ");
        }
        builder.AppendLine();

        for (int r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            builder.Append("| ");
            for (int i = 0; i < columnCount; i++)
            {
                var cell = i < row.Count ? row[i] : string.Empty;
                builder.Append(EscapeMarkdownCell(cell));
                builder.Append(" | ");
            }
            builder.AppendLine();
        }

        return builder.ToString().Trim();
    }

    private static string EscapeMarkdownCell(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        return value.Replace("|", "\\|").Trim();
    }

    private static string GetCellText(TableCell cell)
    {
        var builder = new StringBuilder();

        foreach (var element in cell.Elements())
        {
            switch (element)
            {
                case Paragraph paragraph:
                    {
                        var text = GetParagraphText(paragraph);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            if (builder.Length > 0)
                                builder.Append(' ');
                            builder.Append(text);
                        }
                        break;
                    }

                case Table nestedTable:
                    {
                        var nestedText = GetTableAsMarkdown(nestedTable);
                        if (!string.IsNullOrWhiteSpace(nestedText))
                        {
                            if (builder.Length > 0)
                                builder.Append(' ');
                            builder.Append(nestedText);
                        }
                        break;
                    }
            }
        }

        return NormalizeInlineText(builder.ToString());
    }

    // WORD-BASED CHUNKING (words NEVER break)
    private static List<string> BuildWordBasedChunks(
         List<DocumentBlock> blocks,
        int chunkSize,
        double overlapPercent = 0.20)
    {
        var chunks = new List<string>();
        if (blocks.Count == 0) return chunks;

        var overlapSize = (int)(chunkSize * overlapPercent);
        if (overlapSize < 1) overlapSize = 1;

        var currentWords = new List<string>();
        var currentLength = 0;

        foreach (var rawBlock in blocks)
        {
            // Skip empty blocks. Already normalized in ParseAndChunkAsync.
            //if (string.IsNullOrWhiteSpace(rawBlock)) continue;

            var block = NormalizeDocumentText(rawBlock.BlockContent);
            if (string.IsNullOrWhiteSpace(block)) continue;

            // TABLE
            if (rawBlock.BlockType == "Table")
            {
                if (currentWords.Count > 0)
                {
                    chunks.Add(string.Join(" ", currentWords));
                    currentWords.Clear();
                    currentLength = 0;
                }

                var tableChunks = BuildTableChunks(block, chunkSize, overlapSize);
                chunks.AddRange(tableChunks);

                var tableWords = block.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var ov = tableWords.Skip(Math.Max(0, tableWords.Length - 5)).ToList();
                currentWords = ov;
                currentLength = ov.Sum(w => w.Length + 1);
                continue;
            }

            // NORMAL BLOCK — word-based
            var words = block.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            foreach (var word in words)
            {
                var wordLength = word.Length + 1;

                if (currentLength + wordLength > chunkSize && currentWords.Count > 0)
                {
                    chunks.Add(string.Join(" ", currentWords));

                    var ov = new List<string>();
                    var ovLen = 0;
                    for (int i = currentWords.Count - 1; i >= 0; i--)
                    {
                        var w = currentWords[i].Length + 1;
                        if (ovLen + w > overlapSize) break;
                        ov.Insert(0, currentWords[i]);
                        ovLen += w;
                    }
                    currentWords = ov;
                    currentLength = ovLen;
                }

                currentWords.Add(word);
                currentLength += wordLength;
            }
        }

        if (currentWords.Count > 0)
            chunks.Add(string.Join(" ", currentWords));

        return chunks;
    }

    private static List<string> BuildTableChunks(
        string tableBlock,
        int chunkSize,
        int overlapSize)
    {
        var chunks = new List<string>();

        var lines = tableBlock
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        if (lines.Count < 2)
        {
            chunks.Add(tableBlock);
            return chunks;
        }

        var headerLine = lines[0];
        var separatorLine = lines.Count > 1 ? lines[1] : string.Empty;
        var headerPrefix = headerLine + "\n" + separatorLine + "\n";

        var dataRows = lines.Skip(2).ToList();

        if (dataRows.Count == 0 || tableBlock.Length <= chunkSize)
        {
            chunks.Add(tableBlock);
            return chunks;
        }

        var current = new StringBuilder();
        current.Append(headerPrefix);
        var currentLength = headerPrefix.Length;

        foreach (var row in dataRows)
        {
            var rowLength = row.Length + 1;

            if (currentLength + rowLength > chunkSize &&
                currentLength > headerPrefix.Length)
            {
                chunks.Add(current.ToString().Trim());

                var currentLines = current
                    .ToString()
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Skip(2)
                    .ToList();

                var ovRows = new List<string>();
                var ovLen = 0;
                for (int i = currentLines.Count - 1; i >= 0; i--)
                {
                    var ll = currentLines[i].Length + 1;
                    if (ovLen + ll > overlapSize) break;
                    ovRows.Insert(0, currentLines[i]);
                    ovLen += ll;
                }

                current.Clear();
                current.Append(headerPrefix);
                currentLength = headerPrefix.Length;

                foreach (var ovRow in ovRows)
                {
                    current.AppendLine(ovRow);
                    currentLength += ovRow.Length + 1;
                }
            }

            current.AppendLine(row);
            currentLength += rowLength;
        }

        if (current.Length > headerPrefix.Length)
            chunks.Add(current.ToString().Trim());

        return chunks;
    }

    //private static bool IsTableBlock(string block)
    //{
    //    if (string.IsNullOrWhiteSpace(block)) return false;
    //    var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    //    if (lines.Length < 2) return false;
    //    int tableLines = lines.Count(l => l.TrimStart().StartsWith("|"));
    //    return tableLines >= lines.Length * 0.6;
    //}

    // FIX #3: NormalizeInlineText — lines joined by SPACE
    private static string NormalizeInlineText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        text = text.Replace("\r\n", "\n").Replace("\r", "\n");

        var lines = text.Split('\n')
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line));

        return string.Join(" ", lines).Trim();
    }

    private static string NormalizeDocumentText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        text = text.Replace("\r\n", "\n").Replace("\r", "\n");

        var lines = text.Split('\n')
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line));

        return string.Join(Environment.NewLine, lines);
    }
}