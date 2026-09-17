using Rag.DocumentProcessing.Constants;
using Rag.DocumentProcessing.Interfaces;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Rag.DocumentProcessing.Parsers;

public class PdfDocumentParser : IDocumentParser
{
    public Task<string> ParseAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        using var document =
            PdfDocument.Open(filePath);

        var builder =
            new StringBuilder();

        var fileName =
            Path.GetFileNameWithoutExtension(
                filePath);

        builder.AppendLine(
            $"PDF Document: {fileName}");

        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pageText =
                ExtractPageText(page);

            if (string.IsNullOrWhiteSpace(
                pageText))
            {
                continue;
            }

            builder.AppendLine();

            builder.AppendLine(
                $"Page: {page.Number}");

            builder.AppendLine();

            builder.AppendLine(
                pageText);
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
            PdfDocument.Open(filePath);

        var fileName =
            Path.GetFileNameWithoutExtension(
                filePath);

        var blocks =
            new List<string>();

        blocks.Add(
            $"PDF Document: {fileName}");

        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pageText =
                ExtractPageText(page);

            if (string.IsNullOrWhiteSpace(
                pageText))
            {
                continue;
            }

            var pageBlock =
                new StringBuilder();

            pageBlock.AppendLine(
                $"PDF Document: {fileName}");

            pageBlock.AppendLine(
                $"Page: {page.Number}");

            pageBlock.AppendLine();

            pageBlock.Append(
                pageText);

            blocks.Add(
                NormalizeDocumentText(
                    pageBlock.ToString()));
        }

        if (blocks.Count <= 1)
        {
            return Task.FromResult(
                new List<string>());
        }

        var chunks =
            BuildParentChunks(
                blocks,
                chunkSize);

        return Task.FromResult(chunks);
    }

    private static string ExtractPageText(
        Page page)
    {
        var words =
            page.GetWords()
                .Select(word =>
                    word.Text)
                .Where(text =>
                    !string.IsNullOrWhiteSpace(
                        text))
                .ToList();

        if (words.Count == 0)
        {
            return string.Empty;
        }

        return ReconstructText(
            page,
            words);
    }

    private static string ReconstructText(
        Page page,
        List<string> words)
    {
        var builder =
            new StringBuilder();

        foreach (var word in words)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(word);
        }

        return NormalizePageText(
            builder.ToString());
    }

    private static string NormalizePageText(
        string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        text =
            text
                .Replace("\r\n", "\n")
                .Replace("\r", "\n");

        var lines =
            text
                .Split('\n')
                .Select(line =>
                    NormalizeLine(line))
                .Where(line =>
                    !string.IsNullOrWhiteSpace(line));

        return string.Join(
            Environment.NewLine,
            lines);
    }

    private static List<string> BuildParentChunks(
        List<string> blocks,
        int chunkSize)
    {
        var chunks =
            new List<string>();

        if (blocks.Count == 0)
        {
            return chunks;
        }

        if (chunkSize <= 0)
        {
            chunkSize =
                DocumentConstants.ChunkSize;
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

                current.Append(block);

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

            if (block.Length <= chunkSize)
            {
                current.Append(block);
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
            text
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Split(
                    '\n',
                    StringSplitOptions.RemoveEmptyEntries);

        var current =
            new StringBuilder();

        foreach (var rawLine in lines)
        {
            var line =
                rawLine.Trim();

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var separator =
                current.Length == 0
                    ? string.Empty
                    : Environment.NewLine;

            if (current.Length +
                separator.Length +
                line.Length <=
                chunkSize)
            {
                current.Append(
                    separator);

                current.Append(line);

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

            if (line.Length <= chunkSize)
            {
                current.Append(line);
                continue;
            }

            SplitLongLine(
                line,
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

                current.Append(word);

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

            if (word.Length <= chunkSize)
            {
                current.Append(word);
                continue;
            }

            var start = 0;

            while (start < word.Length)
            {
                var length =
                    Math.Min(
                        chunkSize,
                        word.Length - start);

                result.Add(
                    word.Substring(
                        start,
                        length)
                    .Trim());

                start += length;
            }
        }
    }

    private static string NormalizeDocumentText(
        string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        text =
            text
                .Replace("\r\n", "\n")
                .Replace("\r", "\n");

        var lines =
            text
                .Split('\n')
                .Select(line =>
                    line.Trim())
                .Where(line =>
                    !string.IsNullOrWhiteSpace(line));

        return string.Join(
            Environment.NewLine,
            lines);
    }

    private static string NormalizeLine(
        string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return string.Empty;
        }

        return string.Join(
                " ",
                line.Split(
                    ' ',
                    StringSplitOptions
                        .RemoveEmptyEntries))
            .Trim();
    }
}