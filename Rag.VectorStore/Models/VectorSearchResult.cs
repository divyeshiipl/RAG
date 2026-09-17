namespace Rag.VectorStore.Models;

public class VectorSearchResult
{
    public ulong Id { get; set; }

    public float Score { get; set; }

    public string DocumentId { get; set; } = string.Empty;

    public string DocumentName { get; set; } = string.Empty;

    public int ChunkNumber { get; set; }

    public string Text { get; set; } = string.Empty;
    public string ParentText { get; set; } = string.Empty;
    
}
