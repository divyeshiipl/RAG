namespace Rag.EmbeddingGenerator.Interfaces;

public interface ITextGenerator
{
    Task<string> GenerateAsync(
        string systemPrompt,
        string prompt,
        CancellationToken cancellationToken = default);
}
