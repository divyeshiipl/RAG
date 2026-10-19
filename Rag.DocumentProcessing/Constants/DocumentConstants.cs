namespace Rag.DocumentProcessing.Constants;

public static class DocumentConstants
{
    public const int ChunkSize = 800;  //Characters
    public const int ChildChunkSize = 80;  //Words
    public const int OverlapPer = 20;  //Percentage
    public const int Overlap = 16;  //ChildChunkSize * (OverlapPer / 100);  Words
    public const int ChunkingVersion = 15;
}