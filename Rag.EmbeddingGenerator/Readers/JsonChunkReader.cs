using Rag.DocumentProcessing.Models;
using Rag.DocumentProcessing.Configuration;
using Rag.EmbeddingGenerator.Interfaces;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;

namespace Rag.EmbeddingGenerator.Readers;

public class JsonChunkReader : IChunkReader
{
    private readonly string _chunksRootPath;

    public JsonChunkReader(IOptions<DocumentProcessingOptions> options)
    {
        _chunksRootPath = options.Value.ChunksRootPath;

        if (string.IsNullOrWhiteSpace(_chunksRootPath))
        {
            throw new InvalidOperationException(
                $"Configuration setting '{nameof(DocumentProcessingOptions.ChunksRootPath)}' must be provided.");
        }
    }

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
            DocumentChunk? chunk;

            await using (var stream = File.OpenRead(file))
            {
                chunk = await JsonSerializer.DeserializeAsync<DocumentChunk>(
                    stream,
                    cancellationToken: cancellationToken);
            }  

            if (chunk == null || string.IsNullOrWhiteSpace(chunk.Text))
                continue;

            chunk.ContextualText = BuildContextualText(chunk);
            chunks.Add(chunk);
        }

        return chunks
            .OrderBy(x => x.DocumentId)
            .ThenBy(x => x.ChunkNumber)
            .ToList();
    }
    private static string BuildContextualText(DocumentChunk chunk)
    {
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(chunk.DocumentName))
        {
            builder.AppendLine($"[Document: {chunk.DocumentName}]");
        }

        if (!string.IsNullOrWhiteSpace(chunk.DocumentType))
        {
            builder.AppendLine($"[Document Type: {chunk.DocumentType}]");
        }

        var fileType = DeriveFileType(chunk.RelativePath);
        if (!string.IsNullOrWhiteSpace(fileType))
        {
            builder.AppendLine($"[File Type: {fileType}]");
        }

        if (!string.IsNullOrWhiteSpace(chunk.RelativePath))
        {
            builder.AppendLine($"[Path: {chunk.RelativePath}]");
        }

        builder.AppendLine($"[Chunk: {chunk.ChunkNumber}]");
        builder.AppendLine();
        builder.Append(chunk.Text);

        return builder.ToString();
    }
    private static string DeriveFileType(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return string.Empty;

        var extension = Path.GetExtension(relativePath)
            .TrimStart('.')
            .ToLowerInvariant();

        return extension;
    }
}