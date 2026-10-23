using Rag.EmbeddingGenerator.Interfaces;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Rag.EmbeddingGenerator.Services;

public class OllamaEmbeddingGenerator : IEmbeddingGenerator
{
    private const int MaxWordsForModel = 100;
    private const int MaxCharsForModel = 2000;
    private const int ModelContextSize = 512;
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

        var safeText = PrepareTextForEmbedding(text);

        var requestPayload = new
        {
            model = "all-minilm",
            prompt = safeText,
            truncate = true,
            keep_alive = "30m",
            options = new
            {
                num_ctx = ModelContextSize
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

    private static string PrepareTextForEmbedding(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var normalized = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        if (normalized.Length <= MaxCharsForModel)
        {
            var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length <= MaxWordsForModel)
            {
                return normalized;
            }

            return string.Join(" ", words.Take(MaxWordsForModel));
        }

        var allWords = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var limitedWords = allWords.Take(MaxWordsForModel).ToArray();
        return string.Join(" ", limitedWords).Trim();
    }

    private class OllamaResponse
    {
        [JsonPropertyName("embedding")]
        public float[] Embedding { get; set; } = Array.Empty<float>();
    }
}