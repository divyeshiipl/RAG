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
        string tableId = "",
        string parentBlockId = "",
        string tableContext = "",
        int? pageNumber = null,
        string? sheetName = null,
        string? sqlObjectType = null,
        int? rowStartIndex = null,
        int? rowEndIndex = null,
        IReadOnlyList<string>? rowIdentifiers = null,
        string structuredRowsJson = "");

    Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        float[] queryVector,
        string queryText,      
        int limit = 5);

    Task<IReadOnlyList<VectorSearchResult>> GetDocumentChunksAsync(string documentId);

    Task<IReadOnlyList<VectorSearchResult>> SearchByRowIdentifierAsync(
        string rowIdentifier,
        string? documentId = null,
        string? sheetName = null);
}