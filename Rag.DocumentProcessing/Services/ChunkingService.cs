using DocumentFormat.OpenXml.Drawing.Charts;
using Rag.DocumentProcessing.Constants;
using Rag.DocumentProcessing.Interfaces;
using Rag.DocumentProcessing.Models;
using System.Text;
using System.Text.RegularExpressions;

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

    private static DocumentChunk CreateNewChunk(DocumentInfo doc, int chunkNumber, string text, string parentText)
    {
        return new DocumentChunk
        {
            DocumentId = doc.DocumentId.ToString(),
            DocumentName = doc.DocumentName,
            DocumentType = doc.DocumentType,
            RelativePath = doc.RelativePath,
            ChunkNumber = chunkNumber,
            Text = text.Trim(),
            ParentText = parentText.Trim()
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

        foreach (var parentText in content)
        {
            if (string.IsNullOrWhiteSpace(parentText) || childChunkSize <= 0)
                continue;

            var words = Regex.Matches(parentText, @"\S+")
                .Cast<Match>()
                .Select(match => match.Value)
                .ToList();

            if (words.Count == 0)
                continue;

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
}