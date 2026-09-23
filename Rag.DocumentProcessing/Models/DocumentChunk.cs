namespace Rag.DocumentProcessing.Models;

public class DocumentChunk
{
    public string DocumentId { get; set; } = string.Empty;
    public string DocumentName { get; set; } = string.Empty;
    public string DocumentType { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public int ChunkNumber { get; set; }
    public string Text { get; set; } = string.Empty;
    public string ParentText { get; set; } = string.Empty;
    public string ContextualText { get; set; } = string.Empty;
}