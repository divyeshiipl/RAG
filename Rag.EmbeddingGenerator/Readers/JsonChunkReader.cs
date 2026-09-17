using Rag.DocumentProcessing.Models;
using Rag.EmbeddingGenerator.Interfaces;
using System.Text.Json;

namespace Rag.EmbeddingGenerator.Readers;

public class JsonChunkReader : IChunkReader
{
    private readonly string _chunksRootPath =
        @"C:\Dev\RAG RND without Tools15Sep2026\Chunks";

    public async Task<IReadOnlyList<DocumentChunk>> ReadAllAsync(
        CancellationToken cancellationToken = default)
    {
        var chunks = new List<DocumentChunk>();

        if (!Directory.Exists(_chunksRootPath))
            return chunks;

        var files = Directory.GetFiles(
            _chunksRootPath,
            "chunk-*.json",
            SearchOption.AllDirectories);

        foreach (var file in files)
        {
            await using var stream =
                File.OpenRead(file);

            var chunk =
                await JsonSerializer.DeserializeAsync<DocumentChunk>(
                    stream,
                    cancellationToken: cancellationToken);

            if (chunk != null)
                chunks.Add(chunk);
        }

        return chunks
            .OrderBy(x => x.DocumentId)
            .ThenBy(x => x.ChunkNumber)
            .ToList();
    }
}