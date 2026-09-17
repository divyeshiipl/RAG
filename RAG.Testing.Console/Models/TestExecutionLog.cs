namespace RAG.Testing.Console.Models;

public class TestExecutionLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;
    public string Question { get; set; } = string.Empty;
    public string GeneratedAnswer { get; set; } = string.Empty;
    public List<RetrievedChunkLog> ContextChunks { get; set; } = new();
    public double DurationMilliseconds { get; set; }
}
