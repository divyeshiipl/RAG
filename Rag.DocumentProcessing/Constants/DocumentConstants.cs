namespace Rag.DocumentProcessing.Constants;

public static class DocumentConstants
{
    public const string DocumentsRootPath = @"C:\Dev\RAG RND without Tools15Sep2026\Documents";
    public const string ChunksRootPath = @"C:\Dev\RAG RND without Tools15Sep2026\Chunks";
    public const int ChunkSize = 1000;
    public const int ChildChunkSize = 200;
    public const int OverlapPer = 20;
    public const int Overlap = 40; //ChildChunkSize * (OverlapPer / 100);
}