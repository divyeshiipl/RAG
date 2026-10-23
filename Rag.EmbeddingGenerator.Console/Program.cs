using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Rag.DocumentProcessing.Configuration;
using Rag.EmbeddingGenerator.Interfaces;
using Rag.EmbeddingGenerator.Readers;
using Rag.EmbeddingGenerator.Services;
using Rag.VectorStore.Interfaces;
using Rag.VectorStore.Services;

var services = new ServiceCollection();
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .Build();

services.AddSingleton<IConfiguration>(configuration);
services.AddOptions<DocumentProcessingOptions>()
    .Bind(configuration.GetSection("RagPaths"));

services.AddSingleton<IChunkReader, JsonChunkReader>();
services.AddHttpClient<IEmbeddingGenerator, OllamaEmbeddingGenerator>();
services.AddHttpClient();
services.AddSingleton<IVectorStore>(_ =>
    new QdrantVectorStore("localhost"));

var provider = services.BuildServiceProvider();

var reader = provider.GetRequiredService<IChunkReader>();
var generator = provider.GetRequiredService<IEmbeddingGenerator>();
var vectorStore = provider.GetRequiredService<IVectorStore>();
var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();

Console.WriteLine("Initializing Qdrant...");
await vectorStore.InitializeAsync();
Console.WriteLine("Qdrant initialized.");

var chunks = await reader.ReadAllAsync();

ulong id = 1;

foreach (var chunk in chunks)
{
    Console.WriteLine(
        $"Embedding Document {chunk.DocumentId} - Chunk {chunk.ChunkNumber}");

    var textForEmbedding = chunk.ContextualText;

    var vector = await generator.GenerateAsync(textForEmbedding);

    if (vector.Length != 384)
    {
        throw new InvalidOperationException(
            $"Expected embedding dimension 384, but received {vector.Length}.");
    }

    Console.WriteLine($"Vector dimensions: {vector.Length}");

    await vectorStore.UpsertAsync(
    id,
    vector,
    chunk.ContextualText,
    chunk.ParentText,
    chunk.DocumentName,
    chunk.ChunkNumber,
    chunk.DocumentId,
    chunk.DocumentType,
    fileType: Path.GetExtension(chunk.RelativePath)
        .TrimStart('.')
        .ToLowerInvariant(),
    relativePath: chunk.RelativePath,
    tableId: chunk.TableId,
    parentBlockId: chunk.ParentBlockId,
    tableContext: chunk.TableContext,
    sheetName: string.IsNullOrWhiteSpace(chunk.SheetName)
        ? null
        : chunk.SheetName,
    rowStartIndex: chunk.RowStartIndex,
    rowEndIndex: chunk.RowEndIndex,
    rowIdentifiers: chunk.RowIdentifiers,
    structuredRowsJson: chunk.StructuredRowsJson);

    Console.WriteLine($"Stored vector {id}");

    id++;
    await Task.Delay(100);
}

Console.WriteLine();
Console.WriteLine("All embeddings stored in Qdrant.");
