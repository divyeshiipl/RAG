namespace Rag.DocumentProcessing.Models;

public class DocumentInfo
{
    public int DocumentId { get; set; }

    public int ProjectId { get; set; }

    public string DocumentName { get; set; } = string.Empty;

    public string DocumentType { get; set; } = string.Empty;

    public string FileExtension { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public DateTime CreatedOn { get; set; }
}