using Rag.DocumentProcessing.Constants;
using Rag.DocumentProcessing.Models;

namespace Rag.DocumentProcessing.Interfaces;

public interface IDocumentParser
{
    Task<string> ParseAsync(string filePath,
        CancellationToken cancellationToken = default);

    Task<List<string>> ParseAndChunkAsync(string filePath,
        int chunkSize = DocumentConstants.ChunkSize,
        int childChunkSize = DocumentConstants.ChildChunkSize,
        int overlap = DocumentConstants.Overlap,
        CancellationToken cancellationToken = default);
}