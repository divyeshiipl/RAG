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

        if (string.IsNullOrWhiteSpace(content))
            return chunks;

        // Split text cleanly into sentences using punctuation boundaries
        var rawSentences = Regex.Split(content, @"(?<=[.!?])\s+|\r?\n");
        var builder = new StringBuilder();
        var chunkNumber = 1;

        foreach (var sentence in rawSentences)
        {
            var trimmedSentence = sentence.Trim();
            if (string.IsNullOrEmpty(trimmedSentence))
                continue;

            // If adding the next sentence exceeds the max chunk size, save the current chunk
            if (builder.Length > 0 && builder.Length + trimmedSentence.Length + 1 > maxChunkSize)
            {
                chunks.Add(CreateNewChunk(documentInfo, chunkNumber++, builder.ToString(), string.Empty));
                builder.Clear();
            }

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(trimmedSentence);
        }

        // Add any remaining text as the final chunk
        if (builder.Length > 0)
        {
            chunks.Add(CreateNewChunk(documentInfo, chunkNumber, builder.ToString(), string.Empty));
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
            if (string.IsNullOrWhiteSpace(parentText)) continue;

            int currentIndex = 0;

            // Slide across the parent text using character indices
            while (currentIndex < parentText.Length)
            {
                // Determine the length of the current chunk
                int length = Math.Min(childChunkSize, parentText.Length - currentIndex);

                // Extract the child text chunk
                string childText = parentText.Substring(currentIndex, length).Trim();

                if (!string.IsNullOrEmpty(childText))
                {
                    chunks.Add(CreateNewChunk(documentInfo, globalChunkNumber, childText, parentText));
                    globalChunkNumber++;
                }

                // If we reached the end of the parent string, break out of the loop
                if (currentIndex + childChunkSize >= parentText.Length)
                {
                    break;
                }

                // Move forward by chunk size minus the overlap
                currentIndex += (childChunkSize - overlap);
            }
        }

        return chunks;
    }
}