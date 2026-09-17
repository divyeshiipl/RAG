using Rag.EmbeddingGenerator.Interfaces;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Rag.EmbeddingGenerator.Services;

public class OllamaEmbeddingGenerator : IEmbeddingGenerator
{
    private readonly HttpClient _httpClient;

    public OllamaEmbeddingGenerator(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<float[]> GenerateAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new float[384];
        }

        var requestPayload = new
        {
            model = "all-minilm",
            prompt = text,
            truncate = true, // Instructs Ollama to truncate text exceeding max window
            options = new
            {
                num_ctx = 2048 // Expands context window limit up to model max
            }
        };

        var response = await _httpClient.PostAsJsonAsync(
            "http://localhost:11434/api/embeddings",
            requestPayload,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Ollama API Error ({response.StatusCode}): {errorBody}");
        }

        var result = await response.Content.ReadFromJsonAsync<OllamaResponse>(
            cancellationToken: cancellationToken);

        return result?.Embedding ?? Array.Empty<float>();
    }

    private class OllamaResponse
    {
        [JsonPropertyName("embedding")]
        public float[] Embedding { get; set; } = Array.Empty<float>();
    }
}