using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FoundryWebUI.Models;

namespace FoundryWebUI.Services;

public class FoundryLocalService : ILlmProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<FoundryLocalService> _logger;
    private readonly IConfiguration _configuration;
    private string? _cachedEndpoint;

    public string ProviderName => "foundry";

    public FoundryLocalService(HttpClient httpClient, ILogger<FoundryLocalService> logger, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _logger = logger;
        _configuration = configuration;
    }

    private async Task<string> GetEndpointAsync()
    {
        // Check config first
        var configEndpoint = _configuration["LlmProviders:Foundry:Endpoint"];
        if (!string.IsNullOrEmpty(configEndpoint))
            return configEndpoint.TrimEnd('/');

        // Try to discover via foundry CLI
        if (_cachedEndpoint != null)
            return _cachedEndpoint;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "foundry",
                Arguments = "service status",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = System.Diagnostics.Process.Start(psi);
            if (process != null)
            {
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                // Parse endpoint from output - look for URL pattern
                var match = System.Text.RegularExpressions.Regex.Match(output, @"(https?://[^\s]+)");
                if (match.Success)
                {
                    _cachedEndpoint = match.Value.TrimEnd('/');
                    return _cachedEndpoint;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to discover Foundry Local endpoint via CLI");
        }

        // Default fallback
        return "http://localhost:5273";
    }

    public async Task<ProviderStatus> GetStatusAsync()
    {
        try
        {
            var endpoint = await GetEndpointAsync();
            _httpClient.BaseAddress = null;
            var response = await _httpClient.GetAsync($"{endpoint}/openai/status");
            return new ProviderStatus
            {
                Provider = ProviderName,
                IsAvailable = response.IsSuccessStatusCode,
                Endpoint = endpoint
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
        var models = new List<ModelInfo>();
        try
        {
            var endpoint = await GetEndpointAsync();
            var response = await _httpClient.GetAsync($"{endpoint}/foundry/list");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("data", out var data))
                {
                    foreach (var model in data.EnumerateArray())
                    {
                        models.Add(new ModelInfo
                        {
                            Id = model.GetProperty("id").GetString() ?? "",
                            Name = model.TryGetProperty("name", out var name) ? name.GetString() ?? model.GetProperty("id").GetString() ?? "" : model.GetProperty("id").GetString() ?? "",
                            Description = model.TryGetProperty("description", out var desc) ? desc.GetString() : null,
                            Size = model.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number ? size.GetInt64() : null,
                            Status = "available",
                            Provider = ProviderName,
                            Family = model.TryGetProperty("family", out var fam) ? fam.GetString() : null
                        });
                    }
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var model in doc.RootElement.EnumerateArray())
                    {
                        models.Add(new ModelInfo
                        {
                            Id = model.GetProperty("id").GetString() ?? "",
                            Name = model.TryGetProperty("name", out var name) ? name.GetString() ?? model.GetProperty("id").GetString() ?? "" : model.GetProperty("id").GetString() ?? "",
                            Status = "available",
                            Provider = ProviderName
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get available models from Foundry Local");
        }
        return models;
    }

    public async Task<List<ModelInfo>> GetLoadedModelsAsync()
    {
        var models = new List<ModelInfo>();
        try
        {
            var endpoint = await GetEndpointAsync();
            var response = await _httpClient.GetAsync($"{endpoint}/openai/models");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                var data = doc.RootElement.TryGetProperty("data", out var d) ? d : doc.RootElement;
                if (data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var model in data.EnumerateArray())
                    {
                        models.Add(new ModelInfo
                        {
                            Id = model.GetProperty("id").GetString() ?? "",
                            Name = model.TryGetProperty("name", out var name) ? name.GetString() ?? model.GetProperty("id").GetString() ?? "" : model.GetProperty("id").GetString() ?? "",
                            Status = "loaded",
                            Provider = ProviderName
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get loaded models from Foundry Local");
        }
        return models;
    }

    public async IAsyncEnumerable<ChatResponse> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var endpoint = await GetEndpointAsync();

        // First ensure model is loaded
        try
        {
            await _httpClient.PostAsync($"{endpoint}/openai/load/{Uri.EscapeDataString(request.Model)}", null, cancellationToken);
        }
        catch { /* Model may already be loaded */ }

        var payload = new
        {
            model = request.Model,
            messages = request.Messages.Select(m => new { role = m.Role, content = m.Content }),
            stream = true,
            temperature = request.Temperature
        };

        var jsonContent = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/v1/chat/completions")
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
            if (!line.StartsWith("data: ")) continue;

            var data = line["data: ".Length..];
            if (data == "[DONE]")
            {
                yield return new ChatResponse { Done = true };
                yield break;
            }

            using var doc = JsonDocument.Parse(data);
            var choices = doc.RootElement.GetProperty("choices");
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("content", out var content))
                {
                    yield return new ChatResponse { Content = content.GetString() ?? "" };
                }
            }
        }
    }

    public async IAsyncEnumerable<DownloadProgress> DownloadModelAsync(string modelId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new DownloadProgress { ModelId = modelId, Status = "starting" };

        // Use foundry CLI to download
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "foundry",
            Arguments = $"model download {modelId}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = System.Diagnostics.Process.Start(psi);
        if (process == null)
        {
            yield return new DownloadProgress { ModelId = modelId, Status = "error" };
            yield break;
        }

        while (!process.StandardOutput.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (!string.IsNullOrEmpty(line))
            {
                yield return new DownloadProgress
                {
                    ModelId = modelId,
                    Status = "downloading",
                    Percent = TryParsePercent(line)
                };
            }
        }

        await process.WaitForExitAsync(cancellationToken);
        yield return new DownloadProgress
        {
            ModelId = modelId,
            Status = process.ExitCode == 0 ? "complete" : "error"
        };
    }

    private static double? TryParsePercent(string line)
    {
        var match = System.Text.RegularExpressions.Regex.Match(line, @"(\d+(?:\.\d+)?)%");
        return match.Success && double.TryParse(match.Groups[1].Value, out var pct) ? pct : null;
    }
}
