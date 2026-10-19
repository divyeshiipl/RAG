using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Rag.EmbeddingGenerator.Interfaces;

namespace Rag.EmbeddingGenerator.Services;

public sealed class OllamaTextGenerator : ITextGenerator
{
    private const string Endpoint = "http://localhost:11434/api/generate";
    private const string Model = "llama3.2";
    private readonly HttpClient _httpClient;

    public OllamaTextGenerator(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromMinutes(5);
    }

    public async Task<string> GenerateAsync(
        string systemPrompt,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        var estimatedTokens = (int)Math.Ceiling(
            (systemPrompt.Length + prompt.Length) / 4d);
        var requiredContext = estimatedTokens + 512;
        var contextWindow = Math.Clamp(
            (int)Math.Pow(
                2,
                Math.Ceiling(Math.Log2(Math.Max(2048, requiredContext)))),
            2048,
            8192);

        var requestPayload = new
        {
            model = Model,
            system = systemPrompt,
            prompt,
            stream = false,
            keep_alive = "30m",
            options = new
            {
                num_ctx = contextWindow,
                temperature = 0.1,
                num_predict = 1536
            }
        };

        using var response = await _httpClient.PostAsJsonAsync(
            Endpoint,
            requestPayload,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Ollama text-generation API error ({response.StatusCode}): {errorBody}");
        }

        var result = await response.Content.ReadFromJsonAsync<OllamaResponse>(
            cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(result?.Response))
            throw new InvalidOperationException(
                "Ollama text-generation API returned an empty response.");

        return result.Response.Trim();
    }

    private sealed class OllamaResponse
    {
        [JsonPropertyName("response")]
        public string Response { get; set; } = string.Empty;
    }
}
