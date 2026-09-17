namespace Rag.DocumentProcessing.Configuration;

public class DocumentProcessingOptions
{
    public string DocumentsRootPath { get; set; } = @"C:\Dev\RAG RND\Documents";

    public string ChunksRootPath { get; set; } = @"C:\Dev\RAG RND\Chunks";

    public int ChunkSize { get; set; } = 1000;
}