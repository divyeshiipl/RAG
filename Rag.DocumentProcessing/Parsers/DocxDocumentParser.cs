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
        using var document =
            WordprocessingDocument.Open(filePath, false);

        var body =
            document.MainDocumentPart?.Document.Body;

        if (body is null)
        {
            return Task.FromResult(string.Empty);
        }

        var builder = new StringBuilder();

        foreach (var element in body.Elements())
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (element)
            {
                case Paragraph paragraph:
                    {
                        var text =
                            GetParagraphText(paragraph);

                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            builder.AppendLine(text);
                        }

                        break;
                    }

                case Table table:
                    {
                        var tableText =
                            GetTableText(table);

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
        using var document =
            WordprocessingDocument.Open(filePath, false);

        var body =
            document.MainDocumentPart?.Document.Body;

        if (body is null)
        {
            return Task.FromResult(
                new List<string>());
        }

        var blocks = new List<string>();

        foreach (var element in body.Elements())
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (element)
            {
                case Paragraph paragraph:
                    {
                        var text =
                            GetParagraphText(paragraph);

                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            blocks.Add(text);
                        }

                        break;
                    }

                case Table table:
                    {
                        var tableText =
                            GetTableText(table);

                        if (!string.IsNullOrWhiteSpace(tableText))
                        {
                            blocks.Add(tableText);
                        }

                        break;
                    }
            }
        }

        var parentChunks =
            BuildParentChunks(
                blocks,
                chunkSize);

        return Task.FromResult(parentChunks);
    }

    private static string GetParagraphText(
        Paragraph paragraph)
    {
        var builder =
            new StringBuilder();

        var numberingProperties =
            paragraph
                .ParagraphProperties?
                .NumberingProperties;

        var numberingId =
            numberingProperties?
                .NumberingId?
                .Val?
                .Value;

        var level =
            numberingProperties?
                .NumberingLevelReference?
                .Val?
                .Value ?? 0;

        foreach (var child in paragraph.ChildElements)
        {
            switch (child)
            {
                case Run run:
                    AppendRunText(
                        builder,
                        run);
                    break;

                case Hyperlink hyperlink:
                    AppendElementText(
                        builder,
                        hyperlink);
                    break;

                case SimpleField field:
                    AppendElementText(
                        builder,
                        field);
                    break;

                case SdtRun sdtRun:
                    AppendElementText(
                        builder,
                        sdtRun);
                    break;

                case BookmarkStart:
                case BookmarkEnd:
                    break;

                default:

                    if (!string.IsNullOrWhiteSpace(
                        child.InnerText))
                    {
                        builder.Append(
                            child.InnerText);
                    }

                    break;
            }
        }

        var text =
            NormalizeInlineText(
                builder.ToString());

        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        if (numberingId.HasValue)
        {
            var indentation =
                new string(
                    ' ',
                    level * 2);

            return $"{indentation}- {text}";
        }

        return text;
    }

    private static void AppendRunText(
        StringBuilder builder,
        Run run)
    {
        foreach (var child in run.ChildElements)
        {
            switch (child)
            {
                case Text text:
                    builder.Append(text.Text);
                    break;

                case TabChar:
                    builder.Append('\t');
                    break;

                case Break:
                    builder.Append('\n');
                    break;

                case CarriageReturn:
                    builder.Append('\n');
                    break;

                case NoBreakHyphen:
                    builder.Append('-');
                    break;

                case SoftHyphen:
                    builder.Append('-');
                    break;

                case SymbolChar symbol:
                    if (symbol.Char is not null)
                    {
                        builder.Append(
                            symbol.Char.Value);
                    }

                    break;
            }
        }
    }

    private static void AppendElementText(
        StringBuilder builder,
        OpenXmlElement element)
    {
        foreach (var child in element.Descendants())
        {
            switch (child)
            {
                case Text text:
                    builder.Append(text.Text);
                    break;

                case TabChar:
                    builder.Append('\t');
                    break;

                case Break:
                case CarriageReturn:
                    builder.Append('\n');
                    break;
            }
        }
    }

    private static string GetTableText(
        Table table)
    {
        var rows = table
            .Elements<TableRow>()
            .Select(row =>
                row.Elements<TableCell>()
                    .Select(GetCellText)
                    .Where(text =>
                        !string.IsNullOrWhiteSpace(text))
                    .ToList())
            .Where(row => row.Count > 0)
            .ToList();

        if (rows.Count == 0)
        {
            return string.Empty;
        }

        if (rows.Count == 1)
        {
            return string.Join(
                " | ",
                rows[0]);
        }

        var columnCount =
            rows.Max(row => row.Count);

        if (columnCount == 2)
        {
            return ConvertKeyValueTable(
                rows);
        }

        return ConvertStructuredTable(
            rows);
    }

    private static string ConvertKeyValueTable(
        List<List<string>> rows)
    {
        var builder =
            new StringBuilder();

        var firstRow = rows[0];

        var firstCell =
            firstRow.Count > 0
                ? firstRow[0]
                : string.Empty;

        var secondCell =
            firstRow.Count > 1
                ? firstRow[1]
                : string.Empty;

        var hasHeader =
            IsKeyColumn(firstCell) &&
            IsValueColumn(secondCell);

        var startIndex =
            hasHeader ? 1 : 0;

        for (var i = startIndex;
             i < rows.Count;
             i++)
        {
            var row = rows[i];

            if (row.Count < 2)
            {
                if (row.Count == 1 &&
                    !string.IsNullOrWhiteSpace(
                        row[0]))
                {
                    builder.AppendLine(
                        row[0]);
                }

                continue;
            }

            var key =
                row[0].Trim();

            var value =
                row[1].Trim();

            if (string.IsNullOrWhiteSpace(key) &&
                string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                builder.AppendLine(value);
                continue;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                builder.AppendLine(key);
                continue;
            }

            builder.AppendLine(
                $"{key}: {value}");
        }

        return builder.ToString().Trim();
    }

    private static string ConvertStructuredTable(
        List<List<string>> rows)
    {
        var builder =
            new StringBuilder();

        var headers =
            rows[0];

        for (var rowIndex = 1;
             rowIndex < rows.Count;
             rowIndex++)
        {
            var row =
                rows[rowIndex];

            var values =
                new List<string>();

            for (var columnIndex = 0;
                 columnIndex < row.Count;
                 columnIndex++)
            {
                var value =
                    row[columnIndex].Trim();

                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (columnIndex < headers.Count)
                {
                    var header =
                        headers[columnIndex].Trim();

                    if (!string.IsNullOrWhiteSpace(
                        header))
                    {
                        values.Add(
                            $"{header}: {value}");

                        continue;
                    }
                }

                values.Add(value);
            }

            if (values.Count == 0)
            {
                continue;
            }

            builder.AppendLine(
                string.Join(
                    ", ",
                    values));

            if (rowIndex < rows.Count - 1)
            {
                builder.AppendLine();
            }
        }

        return builder.ToString().Trim();
    }

    private static string GetCellText(
        TableCell cell)
    {
        var builder =
            new StringBuilder();

        foreach (var element in cell.Elements())
        {
            switch (element)
            {
                case Paragraph paragraph:
                    {
                        var text =
                            GetParagraphText(
                                paragraph);

                        if (!string.IsNullOrWhiteSpace(
                            text))
                        {
                            if (builder.Length > 0)
                            {
                                builder.Append(' ');
                            }

                            builder.Append(text);
                        }

                        break;
                    }

                case Table nestedTable:
                    {
                        var nestedText =
                            GetTableText(
                                nestedTable);

                        if (!string.IsNullOrWhiteSpace(
                            nestedText))
                        {
                            if (builder.Length > 0)
                            {
                                builder.Append(' ');
                            }

                            builder.Append(
                                nestedText);
                        }

                        break;
                    }
            }
        }

        return NormalizeInlineText(
            builder.ToString());
    }

    private static bool IsKeyColumn(
        string text)
    {
        var value =
            text.Trim()
                .ToLowerInvariant();

        return value is
            "field" or
            "name" or
            "property" or
            "attribute" or
            "key" or
            "parameter" or
            "item";
    }

    private static bool IsValueColumn(
        string text)
    {
        var value =
            text.Trim()
                .ToLowerInvariant();

        return value is
            "value" or
            "description" or
            "details" or
            "value/details" or
            "information" or
            "result";
    }

    private static List<string> BuildParentChunks(
        List<string> blocks,
        int chunkSize)
    {
        var parentChunks =
            new List<string>();

        if (blocks.Count == 0)
        {
            return parentChunks;
        }

        var currentParent =
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
                currentParent.Length == 0
                    ? string.Empty
                    : Environment.NewLine +
                      Environment.NewLine;

            var candidateLength =
                currentParent.Length +
                separator.Length +
                block.Length;

            if (candidateLength <= chunkSize)
            {
                currentParent.Append(
                    separator);

                currentParent.Append(
                    block);

                continue;
            }

            if (currentParent.Length > 0)
            {
                parentChunks.Add(
                    currentParent
                        .ToString()
                        .Trim());

                currentParent.Clear();
            }

            if (block.Length <= chunkSize)
            {
                currentParent.Append(
                    block);

                continue;
            }

            var largeBlocks =
                SplitLargeBlock(
                    block,
                    chunkSize);

            for (var i = 0;
                 i < largeBlocks.Count - 1;
                 i++)
            {
                parentChunks.Add(
                    largeBlocks[i]);
            }

            if (largeBlocks.Count > 0)
            {
                currentParent.Append(
                    largeBlocks[^1]);
            }
        }

        if (currentParent.Length > 0)
        {
            parentChunks.Add(
                currentParent
                    .ToString()
                    .Trim());
        }

        return parentChunks;
    }

    private static List<string> SplitLargeBlock(
        string text,
        int chunkSize)
    {
        var result =
            new List<string>();

        var paragraphs =
            text.Split(
                new[]
                {
                    "\r\n\r\n",
                    "\n\n"
                },
                StringSplitOptions.RemoveEmptyEntries);

        var current =
            new StringBuilder();

        foreach (var paragraph in paragraphs)
        {
            var cleanParagraph =
                paragraph.Trim();

            if (string.IsNullOrWhiteSpace(
                cleanParagraph))
            {
                continue;
            }

            var separator =
                current.Length == 0
                    ? string.Empty
                    : Environment.NewLine +
                      Environment.NewLine;

            if (current.Length +
                separator.Length +
                cleanParagraph.Length <=
                chunkSize)
            {
                current.Append(
                    separator);

                current.Append(
                    cleanParagraph);

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

            if (cleanParagraph.Length <=
                chunkSize)
            {
                current.Append(
                    cleanParagraph);

                continue;
            }

            var words =
                cleanParagraph.Split(
                    ' ',
                    StringSplitOptions.RemoveEmptyEntries);

            foreach (var word in words)
            {
                var wordSeparator =
                    current.Length == 0
                        ? string.Empty
                        : " ";

                if (current.Length +
                    wordSeparator.Length +
                    word.Length <=
                    chunkSize)
                {
                    current.Append(
                        wordSeparator);

                    current.Append(
                        word);
                }
                else
                {
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

    private static string NormalizeInlineText(
        string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        text = text
            .Replace("\r\n", "\n")
            .Replace("\r", "\n");

        var lines =
            text.Split('\n')
                .Select(line =>
                    string.Join(
                        " ",
                        line.Split(
                            ' ',
                            StringSplitOptions
                                .RemoveEmptyEntries)))
                .Where(line =>
                    !string.IsNullOrWhiteSpace(
                        line));

        return string.Join(
            " ",
            lines).Trim();
    }

    private static string NormalizeDocumentText(
        string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        text = text
            .Replace("\r\n", "\n")
            .Replace("\r", "\n");

        var lines =
            text.Split('\n')
                .Select(line => line.Trim())
                .Where(line =>
                    !string.IsNullOrWhiteSpace(
                        line));

        return string.Join(
            Environment.NewLine,
            lines);
    }
}