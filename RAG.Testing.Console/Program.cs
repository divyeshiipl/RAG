using Microsoft.Extensions.DependencyInjection;
using Rag.EmbeddingGenerator.Interfaces;
using Rag.EmbeddingGenerator.Readers;
using Rag.EmbeddingGenerator.Services;
using Rag.VectorStore.Interfaces;
using Rag.VectorStore.Services;
using RAG.Testing.Console.Services;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;

var services = new ServiceCollection();
services.AddSingleton<IChunkReader, JsonChunkReader>();
services.AddHttpClient<IEmbeddingGenerator, OllamaEmbeddingGenerator>();
services.AddHttpClient();
services.AddSingleton<IVectorStore>(_ =>
    new QdrantVectorStore("localhost"));

var provider = services.BuildServiceProvider();
var generator = provider.GetRequiredService<IEmbeddingGenerator>();
var vectorStore = provider.GetRequiredService<IVectorStore>();
var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();

using var llmClient = httpClientFactory.CreateClient();
llmClient.Timeout = TimeSpan.FromMinutes(2);

var fileLogger = new TestLogger(@"C:\Dev\RAG RND without Tools15Sep2026\Logs\ResultLogs\rag_execution_history.log");

while (true)
{
    Console.Write("\nEnter Question (or type 'exit' to quit): ");
    string? userQuestion = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(userQuestion) || userQuestion.Equals("exit", StringComparison.OrdinalIgnoreCase))
        break;

    var stopwatch = Stopwatch.StartNew();

    Console.WriteLine("\nVectorizing question & querying Qdrant...");

    float[] queryVector =
    await generator.GenerateAsync(userQuestion);

    var relevantResults =
        await vectorStore.SearchAsync(
            queryVector, limit: 5);

    if (relevantResults.Count == 0)
    {
        Console.WriteLine("No context found in Qdrant.");
        continue;
    }

    var contextBuilder = new StringBuilder();

    foreach (var match in relevantResults)
    {
        contextBuilder.AppendLine(
            $"--- [Document: {match.DocumentName} | Chunk: {match.ChunkNumber}] ---");

        contextBuilder.AppendLine(
            string.IsNullOrWhiteSpace(match.ParentText)
                ? match.Text
                : match.ParentText);

        contextBuilder.AppendLine();
    }

    string promptPayload = $"""
        You are an accurate documentation assistant.

        Answer the user's question using ONLY the provided documentation context.

        Rules:
        - Use only information explicitly present in the context.
        - Do not use your own knowledge.
        - Do not guess or infer missing information.
        - If the question asks for a list, identify ALL items belonging to that list.
        - Do not summarize a list when the individual items are available in the context.
        - Preserve every relevant item from the documentation.
        - If multiple chunks contain parts of the same list or section, combine them.
        - Do not stop after finding a partial answer.
        - Before producing the final answer, verify that all relevant items from the context have been included.
        - If a requested detail is not present in the context, explicitly say "Not found in the provided documentation."

        Context:
        {contextBuilder}

        Question:
        {userQuestion}

        Answer:
        """;

    var requestBody = new
    {
        model = "llama3.2",
        prompt = promptPayload,
        stream = false,
        options = new
        {
            num_ctx = 8192,
            temperature = 0.1,
            num_predict = 1024
        }
    };

    Console.WriteLine("Sending context to local LLM...");
    var response = await llmClient.PostAsJsonAsync("http://localhost:11434/api/generate", requestBody);
    response.EnsureSuccessStatusCode();

    var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>();
    stopwatch.Stop();

    string aiAnswer = result?.Response?.Trim() ?? "No answer generated.";

    await fileLogger.AppendTestResultAsync(userQuestion, aiAnswer, stopwatch.ElapsedMilliseconds);

    Console.WriteLine("\n--- AI Answer ---");
    Console.WriteLine(aiAnswer);
    Console.WriteLine("\n--- Docs Referenced ---");
    Console.WriteLine(string.Join(", ", relevantResults.Select(x => x.DocumentName).Distinct().ToList()));   
    Console.WriteLine("-----------------");
    Console.WriteLine($"Logged result. Execution Time: {stopwatch.ElapsedMilliseconds} ms");
}

Console.WriteLine("\nSession ended.");

public record OllamaGenerateResponse(
    [property: JsonPropertyName("response")] string Response);