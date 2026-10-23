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
    public string TableId { get; set; } = string.Empty;
    public string ParentBlockId { get; set; } = string.Empty;
    public string TableContext { get; set; } = string.Empty;
    public string SheetName { get; set; } = string.Empty;
    public int? RowStartIndex { get; set; }
    public int? RowEndIndex { get; set; }
    public List<string> RowIdentifiers { get; set; } = [];
    public string StructuredRowsJson { get; set; } = string.Empty;
}