using Rag.DocumentProcessing.Constants;
using Rag.DocumentProcessing.Interfaces;
using System.Text;

namespace Rag.DocumentProcessing.Parsers;

public class TextDocumentParser : IDocumentParser
{
    public async Task<string> ParseAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var content =
            await File.ReadAllTextAsync(
                filePath,
                Encoding.UTF8,
                cancellationToken);

        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        content =
            NormalizeDocumentText(content);

        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        if (!HasDocumentTitle(content))
        {
            var fileName =
                Path.GetFileNameWithoutExtension(
                    filePath);

            content =
                $"Document: {fileName}" +
                Environment.NewLine +
                Environment.NewLine +
                content;
        }

        return content;
    }

    public async Task<List<string>> ParseAndChunkAsync(
        string filePath,
        int chunkSize = DocumentConstants.ChunkSize,
        int childChunkSize = DocumentConstants.ChildChunkSize,
        int overlap = DocumentConstants.Overlap,
        CancellationToken cancellationToken = default)
    {
        var content =
            await File.ReadAllTextAsync(
                filePath,
                Encoding.UTF8,
                cancellationToken);

        if (string.IsNullOrWhiteSpace(content))
        {
            return new List<string>();
        }

        content =
            NormalizeDocumentText(content);

        if (string.IsNullOrWhiteSpace(content))
        {
            return new List<string>();
        }

        if (!HasDocumentTitle(content))
        {
            var fileName =
                Path.GetFileNameWithoutExtension(
                    filePath);

            content =
                $"Document: {fileName}" +
                Environment.NewLine +
                Environment.NewLine +
                content;
        }

        var blocks =
            ExtractLogicalBlocks(content);

        if (blocks.Count == 0)
        {
            return new List<string>();
        }

        return BuildParentChunks(
            blocks,
            chunkSize);
    }

    private static List<string> ExtractLogicalBlocks(
        string content)
    {
        var blocks =
            new List<string>();

        var lines =
            content
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Split('\n');

        var currentBlock =
            new StringBuilder();

        foreach (var rawLine in lines)
        {
            var line =
                rawLine.Trim();

            if (string.IsNullOrWhiteSpace(line))
            {
                AddCurrentBlock(
                    blocks,
                    currentBlock);

                continue;
            }

            if (IsHeading(line) &&
                currentBlock.Length > 0)
            {
                AddCurrentBlock(
                    blocks,
                    currentBlock);
            }

            if (currentBlock.Length > 0)
            {
                currentBlock.AppendLine();
            }

            currentBlock.Append(line);

            if (IsHeading(line))
            {
                AddCurrentBlock(
                    blocks,
                    currentBlock);
            }
        }

        AddCurrentBlock(
            blocks,
            currentBlock);

        return blocks;
    }

    private static void AddCurrentBlock(
        List<string> blocks,
        StringBuilder currentBlock)
    {
        if (currentBlock.Length == 0)
        {
            return;
        }

        var block =
            NormalizeDocumentText(
                currentBlock.ToString());

        if (!string.IsNullOrWhiteSpace(block))
        {
            blocks.Add(block);
        }

        currentBlock.Clear();
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

            if (candidateLength <= chunkSize)
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

                var part =
                    word.Substring(
                        start,
                        length);

                result.Add(
                    part.Trim());

                start += length;
            }
        }
    }

    private static bool HasDocumentTitle(
        string content)
    {
        var firstNonEmptyLine =
            content
                .Split(
                    new[] { "\r\n", "\n", "\r" },
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(
            firstNonEmptyLine))
        {
            return false;
        }

        return IsHeading(
            firstNonEmptyLine);
    }

    private static bool IsHeading(
        string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var value =
            line.Trim();

        if (value.StartsWith("#"))
        {
            return true;
        }

        if (value.Length <= 120 &&
            value.EndsWith(":"))
        {
            return true;
        }

        if (value.Length <= 120 &&
            value.StartsWith(
                "Chapter ",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value.Length <= 120 &&
            value.StartsWith(
                "Section ",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
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
                    NormalizeLine(line))
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