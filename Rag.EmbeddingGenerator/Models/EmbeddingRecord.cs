namespace Rag.EmbeddingGenerator.Models;

public class EmbeddingRecord
{
    public string DocumentId { get; set; } = string.Empty;

    public int ChunkNumber { get; set; }

    public string Text { get; set; } = string.Empty;

    public float[] Embedding { get; set; } = [];
}