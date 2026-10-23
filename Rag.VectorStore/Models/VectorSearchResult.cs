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
    public string TableId { get; set; } = string.Empty;
    public string ParentBlockId { get; set; } = string.Empty;
    public string TableContext { get; set; } = string.Empty;

    public string DocumentType { get; set; } = string.Empty;
    public string FileType { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public int? PageNumber { get; set; }
    public string? SheetName { get; set; }
    public string? SqlObjectType { get; set; }
    public int? RowStartIndex { get; set; }
    public int? RowEndIndex { get; set; }
    public IReadOnlyList<string> RowIdentifiers { get; set; } = [];
    public string StructuredRowsJson { get; set; } = string.Empty;
    public bool IsStructuredMatch { get; set; }

}
