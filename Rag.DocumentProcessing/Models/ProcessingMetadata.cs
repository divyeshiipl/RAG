namespace Rag.DocumentProcessing.Models;

public class ProcessingMetadata
{
    public int DocumentId { get; set; }

    public string DocumentName { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public string FileHash { get; set; } = string.Empty;

    public DateTime ProcessedOn { get; set; }

    public int TotalChunks { get; set; }

    public int ChunkingVersion { get; set; }
}