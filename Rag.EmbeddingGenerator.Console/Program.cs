using Microsoft.Extensions.DependencyInjection;
using Rag.EmbeddingGenerator.Interfaces;
using Rag.EmbeddingGenerator.Readers;
using Rag.EmbeddingGenerator.Services;
using Rag.VectorStore.Interfaces;
using Rag.VectorStore.Services;

var services = new ServiceCollection();

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

    var vector = await generator.GenerateAsync(chunk.Text);

    if (vector.Length != 384)
    {
        throw new InvalidOperationException(
            $"Expected embedding dimension 384, but received {vector.Length}.");
    }

    Console.WriteLine($"Vector dimensions: {vector.Length}");

    await vectorStore.UpsertAsync(
        id,
        vector,
        chunk.Text,
        chunk.ParentText,
        chunk.DocumentName,
        chunk.ChunkNumber,
        chunk.DocumentId);

    Console.WriteLine($"Stored vector {id}");

    id++;
    await Task.Delay(100);
}

Console.WriteLine();
Console.WriteLine("All embeddings stored in Qdrant.");
Console.ReadKey();
