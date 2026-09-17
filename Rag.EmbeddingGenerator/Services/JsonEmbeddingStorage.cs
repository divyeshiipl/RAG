using System.Text.Json;
using Rag.EmbeddingGenerator.Constants;
using Rag.EmbeddingGenerator.Interfaces;
using Rag.EmbeddingGenerator.Models;

namespace Rag.EmbeddingGenerator.Services;

public class JsonEmbeddingStorage : IEmbeddingStorage
{
    public async Task SaveAsync(
        IEnumerable<EmbeddingRecord> embeddings,
        CancellationToken cancellationToken = default)
    {
        foreach (var embedding in embeddings)
        {
            var directory = Path.Combine(
                EmbeddingConstants.EmbeddingsRootPath,
                embedding.DocumentId);

            Directory.CreateDirectory(directory);

            var filePath = Path.Combine(
                directory,
                $"embedding-{embedding.ChunkNumber:D3}.json");

            var json = JsonSerializer.Serialize(
                embedding,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });

            await File.WriteAllTextAsync(
                filePath,
                json,
                cancellationToken);
        }
    }
}