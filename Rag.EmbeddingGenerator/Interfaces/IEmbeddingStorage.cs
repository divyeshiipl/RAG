using Rag.EmbeddingGenerator.Models;

namespace Rag.EmbeddingGenerator.Interfaces;

public interface IEmbeddingStorage
{
    Task SaveAsync(
        IEnumerable<EmbeddingRecord> embeddings,
        CancellationToken cancellationToken = default);
}