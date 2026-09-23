using System.Text.Json;
using Microsoft.Extensions.Options;
using Rag.DocumentProcessing.Configuration;
using Rag.EmbeddingGenerator.Interfaces;
using Rag.EmbeddingGenerator.Models;

namespace Rag.EmbeddingGenerator.Services;

public class JsonEmbeddingStorage : IEmbeddingStorage
{
    private readonly string _embeddingsRootPath;

    public JsonEmbeddingStorage(IOptions<DocumentProcessingOptions> options)
    {
        _embeddingsRootPath = options.Value.EmbeddingsRootPath;

        if (string.IsNullOrWhiteSpace(_embeddingsRootPath))
        {
            throw new InvalidOperationException(
                $"Configuration setting '{nameof(DocumentProcessingOptions.EmbeddingsRootPath)}' must be provided.");
        }
    }

    public async Task SaveAsync(
        IEnumerable<EmbeddingRecord> embeddings,
        CancellationToken cancellationToken = default)
    {
        foreach (var embedding in embeddings)
        {
            var directory = Path.Combine(
                _embeddingsRootPath,
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