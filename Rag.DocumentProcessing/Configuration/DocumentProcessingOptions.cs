namespace Rag.DocumentProcessing.Configuration;

public class DocumentProcessingOptions
{
    public string DocumentsRootPath { get; set; } = string.Empty;

    public string ChunksRootPath { get; set; } = string.Empty;

    public string EmbeddingsRootPath { get; set; } = string.Empty;

    public string LogsRootPath { get; set; } = string.Empty;

    public int ChunkSize { get; set; } = 1000;
}