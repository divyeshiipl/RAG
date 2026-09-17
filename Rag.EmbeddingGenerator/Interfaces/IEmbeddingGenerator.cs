namespace Rag.EmbeddingGenerator.Interfaces;

public interface IEmbeddingGenerator
{
    Task<float[]> GenerateAsync(
        string text,
        CancellationToken cancellationToken = default);
}