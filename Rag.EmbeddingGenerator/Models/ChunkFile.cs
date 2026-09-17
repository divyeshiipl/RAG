namespace Rag.EmbeddingGenerator.Models;

public class ChunkFile
{
    public int DocumentId { get; set; }

    public int ChunkNumber { get; set; }

    public string Text { get; set; } = string.Empty;
}