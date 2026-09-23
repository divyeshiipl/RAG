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
        string documentName,
        int chunkNumber,
        string documentId,
        string documentType = "",
        string fileType = "",
        string relativePath = "",
        int? pageNumber = null,
        string? sheetName = null,
        string? sqlObjectType = null);

    Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        float[] queryVector,
        string queryText,      
        int limit = 5);
}