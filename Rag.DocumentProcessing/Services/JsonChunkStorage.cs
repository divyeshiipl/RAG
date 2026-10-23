using System.Text.Json;
using Microsoft.Extensions.Options;
using Rag.DocumentProcessing.Configuration;
using Rag.DocumentProcessing.Constants;
using Rag.DocumentProcessing.Interfaces;
using Rag.DocumentProcessing.Models;

namespace Rag.DocumentProcessing.Services;

public class JsonChunkStorage : IChunkStorage
{
    private readonly string _chunksRootPath;

    public JsonChunkStorage(IOptions<DocumentProcessingOptions> options)
    {
        _chunksRootPath = RequirePath(
            options.Value.ChunksRootPath,
            nameof(DocumentProcessingOptions.ChunksRootPath));
    }

    private readonly JsonSerializerOptions _jsonOptions =
        new()
        {
            WriteIndented = true
        };

    public async Task SaveAsync(
        DocumentInfo document,
        string contentHash,
        IEnumerable<DocumentChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        var chunkList = chunks.ToList();

        var documentFolder =
            Path.Combine(
                _chunksRootPath,
                document.DocumentId.ToString());

        Directory.CreateDirectory(documentFolder);

        // Delete old chunk files before saving the new chunks.
        foreach (var file in Directory.GetFiles(
                     documentFolder,
                     "chunk-*.json"))
        {
            File.Delete(file);
        }

        foreach (var chunk in chunkList)
        {
            chunk.DocumentName = document.DocumentName;

            var filePath =
                Path.Combine(
                    documentFolder,
                    $"chunk-{chunk.ChunkNumber:D3}.json");

            var json =
                JsonSerializer.Serialize(
                    chunk,
                    _jsonOptions);

            await File.WriteAllTextAsync(
                filePath,
                json,
                cancellationToken);
        }

        var metadata =
            new ProcessingMetadata
            {
                DocumentId = document.DocumentId,
                DocumentName = document.DocumentName,
                RelativePath = document.RelativePath,
                FileHash = contentHash,
                ProcessedOn = DateTime.Now,
                TotalChunks = chunkList.Count,
                ChunkingVersion = DocumentConstants.ChunkingVersion
            };

        var metadataPath =
            Path.Combine(
                documentFolder,
                "metadata.json");

        await File.WriteAllTextAsync(
            metadataPath,
            JsonSerializer.Serialize(
                metadata,
                _jsonOptions),
            cancellationToken);
    }

    public async Task<ProcessingMetadata?> GetMetadataAsync(
        int documentId,
        CancellationToken cancellationToken = default)
    {
        var metadataPath =
            Path.Combine(
                _chunksRootPath,
                documentId.ToString(),
                "metadata.json");

        if (!File.Exists(metadataPath))
        {
            return null;
        }

        var json =
            await File.ReadAllTextAsync(
                metadataPath,
                cancellationToken);

        return JsonSerializer.Deserialize<ProcessingMetadata>(
            json);
    }

    public async Task<IReadOnlyList<ProcessingMetadata>>
        GetAllMetadataAsync(
            CancellationToken cancellationToken = default)
    {
        var result =
            new List<ProcessingMetadata>();

        if (!Directory.Exists(_chunksRootPath))
        {
            return result;
        }

        var documentFolders =
            Directory.GetDirectories(_chunksRootPath);

        foreach (var folder in documentFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var metadataPath =
                Path.Combine(
                    folder,
                    "metadata.json");

            if (!File.Exists(metadataPath))
            {
                continue;
            }

            var json =
                await File.ReadAllTextAsync(
                    metadataPath,
                    cancellationToken);

            var metadata =
                JsonSerializer.Deserialize<ProcessingMetadata>(
                    json);

            if (metadata is not null)
            {
                result.Add(metadata);
            }
        }

        return result;
    }

    public Task DeleteAsync(
        int documentId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var documentFolder =
            Path.Combine(
                _chunksRootPath,
                documentId.ToString());

        if (Directory.Exists(documentFolder))
        {
            Directory.Delete(
                documentFolder,
                recursive: true);
        }

        return Task.CompletedTask;
    }

    private static string RequirePath(string path, string settingName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(
                $"Configuration setting '{settingName}' must be provided.");
        }

        return path;
    }
}