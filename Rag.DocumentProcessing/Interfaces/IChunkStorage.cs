using Rag.DocumentProcessing.Models;

namespace Rag.DocumentProcessing.Interfaces;

public interface IChunkStorage
{
    Task<ProcessingMetadata?> GetMetadataAsync(
        int documentId,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        DocumentInfo document,
        string fileHash,
        IEnumerable<DocumentChunk> chunks,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        int documentId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProcessingMetadata>> GetAllMetadataAsync(
        CancellationToken cancellationToken = default);
}