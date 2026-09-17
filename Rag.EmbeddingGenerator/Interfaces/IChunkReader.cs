using Rag.DocumentProcessing.Models;
using Rag.EmbeddingGenerator.Models;

namespace Rag.EmbeddingGenerator.Interfaces;

public interface IChunkReader
{
    Task<IReadOnlyList<DocumentChunk>> ReadAllAsync(
        CancellationToken cancellationToken = default);
}