using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using FoundryWebUI.Models;

namespace FoundryWebUI.Services;

public class OllamaService : ILlmProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OllamaService> _logger;
    private readonly string _endpoint;

    public string ProviderName => "ollama";

    public OllamaService(HttpClient httpClient, ILogger<OllamaService> logger, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _logger = logger;
        _endpoint = (configuration["LlmProviders:Ollama:Endpoint"] ?? "http://localhost:11434").TrimEnd('/');
    }

    public async Task<ProviderStatus> GetStatusAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_endpoint}/api/tags");
            return new ProviderStatus
            {
                Provider = ProviderName,
                IsAvailable = response.IsSuccessStatusCode,
                Endpoint = _endpoint
            };
        }
        catch (Exception ex)
        {
            return new ProviderStatus
            {
                Provider = ProviderName,
                IsAvailable = false,
                Error = ex.Message
            };
        }
    }

    public async Task<List<ModelInfo>> GetAvailableModelsAsync()
    {
        // Ollama doesn't have a separate catalog — local models are what's available
        return await GetLoadedModelsAsync();
    }

    public async Task<List<ModelInfo>> GetLoadedModelsAsync()
    {
        var models = new List<ModelInfo>();
        try
        {
            var response = await _httpClient.GetAsync($"{_endpoint}/api/tags");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("models", out var modelsArray))
                {
                    foreach (var model in modelsArray.EnumerateArray())
                    {
                        var name = model.GetProperty("name").GetString() ?? "";
                        models.Add(new ModelInfo
                        {
                            Id = name,
                            Name = name,
                            Size = model.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number ? size.GetInt64() : null,
                            Status = "downloaded",
                            Provider = ProviderName,
                            ParameterSize = model.TryGetProperty("details", out var details) && details.TryGetProperty("parameter_size", out var ps) ? ps.GetString() : null,
                            Family = details.ValueKind != JsonValueKind.Undefined && details.TryGetProperty("family", out var fam) ? fam.GetString() : null
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get models from Ollama");
        }
        return models;
    }

    public async IAsyncEnumerable<ChatResponse> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            model = request.Model,
            messages = request.Messages.Select(m => new { role = m.Role, content = m.Content }),
            stream = true
        };

        var jsonContent = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint}/api/chat")
        {
            Content = jsonContent
        };

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new System.IO.StreamReader(stream);

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrEmpty(line)) continue;

            using var doc = JsonDocument.Parse(line);
            var done = doc.RootElement.TryGetProperty("done", out var d) && d.GetBoolean();

            if (doc.RootElement.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content))
            {
                yield return new ChatResponse
                {
                    Content = content.GetString() ?? "",
                    Done = done
                };
            }

            if (done) yield break;
        }
    }

    public async IAsyncEnumerable<DownloadProgress> DownloadModelAsync(string modelId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var payload = new { model = modelId, stream = true };
        var jsonContent = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint}/api/pull")
        {
            Content = jsonContent
        };

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new System.IO.StreamReader(stream);

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrEmpty(line)) continue;

            using var doc = JsonDocument.Parse(line);
            var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
            var total = doc.RootElement.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : (long?)null;
            var completed = doc.RootElement.TryGetProperty("completed", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt64() : (long?)null;

            yield return new DownloadProgress
            {
                ModelId = modelId,
                Status = status,
                Total = total,
                Completed = completed,
                Percent = total > 0 && completed.HasValue ? Math.Round((double)completed.Value / total.Value * 100, 1) : null
            };

            if (status == "success")
                yield break;
        }
    }
}
