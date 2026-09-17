using Rag.VectorStore.Models;

namespace Rag.VectorStore.Interfaces;

public interface IVectorStore
{
    Task InitializeAsync();

    Task UpsertAsync(
        ulong id,
        float[] vector,
        string text,
        string parentText,
        string documentName, int chunkNumber, string documentId);

    Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        float[] queryVector,
        int limit = 5);
}
