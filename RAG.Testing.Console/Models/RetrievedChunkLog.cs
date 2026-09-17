namespace RAG.Testing.Console.Models;

public class RetrievedChunkLog
{
    public ulong PointId { get; set; }
    public float Score { get; set; }
    public string DocumentName { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}
