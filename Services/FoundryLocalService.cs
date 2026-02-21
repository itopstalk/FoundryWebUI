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

    // Cache the catalog so we can look up URIs for download
    private List<JsonElement>? _catalogCache;

    public string ProviderName => "foundry";

    public FoundryLocalService(HttpClient httpClient, ILogger<FoundryLocalService> logger, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _logger = logger;
        _configuration = configuration;
    }

    private async Task<string> GetEndpointAsync()
    {
        var configEndpoint = _configuration["LlmProviders:Foundry:Endpoint"];
        if (!string.IsNullOrEmpty(configEndpoint))
            return configEndpoint.TrimEnd('/');

        if (_cachedEndpoint != null)
            return _cachedEndpoint;

        // Primary: discover via foundry CLI (port is random on each start)
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
                var outputTask = process.StandardOutput.ReadToEndAsync();
                if (process.WaitForExit(15000))
                {
                    var output = await outputTask;
                    // Output contains: "http://127.0.0.1:49681/openai/status" — extract base URL
                    var match = System.Text.RegularExpressions.Regex.Match(output, @"(https?://[\d.]+:\d+)");
                    if (match.Success)
                    {
                        _cachedEndpoint = match.Value.TrimEnd('/');
                        _logger.LogInformation("Discovered Foundry Local at {Endpoint}", _cachedEndpoint);
                        return _cachedEndpoint;
                    }
                }
                else
                {
                    try { process.Kill(); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to discover Foundry Local endpoint via CLI");
        }

        // Fallback: probe common ports
        foreach (var port in new[] { 5272, 5273, 5274 })
        {
            try
            {
                var testUrl = $"http://localhost:{port}/openai/status";
                using var cts = new CancellationTokenSource(3000);
                var resp = await _httpClient.GetAsync(testUrl, cts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    _cachedEndpoint = $"http://localhost:{port}";
                    _logger.LogInformation("Discovered Foundry Local at {Endpoint} via port scan", _cachedEndpoint);
                    return _cachedEndpoint;
                }
            }
            catch { }
        }

        return "http://localhost:5272";
    }

    public async Task<ProviderStatus> GetStatusAsync()
    {
        try
        {
            var endpoint = await GetEndpointAsync();
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

    public async Task<ProviderStatus> ReconnectAsync()
    {
        _cachedEndpoint = null;
        _catalogCache = null;
        _logger.LogInformation("Foundry Local endpoint cache cleared, re-discovering...");

        // If there's an explicit endpoint in config, verify it directly
        var configEndpoint = _configuration["LlmProviders:Foundry:Endpoint"];
        if (!string.IsNullOrEmpty(configEndpoint))
        {
            try
            {
                using var cts = new CancellationTokenSource(5000);
                var resp = await _httpClient.GetAsync($"{configEndpoint.TrimEnd('/')}/openai/status", cts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    _cachedEndpoint = configEndpoint.TrimEnd('/');
                    return new ProviderStatus { Provider = ProviderName, IsAvailable = true, Endpoint = _cachedEndpoint };
                }
            }
            catch { }

            // Config endpoint didn't respond — return unavailable with helpful message
            return new ProviderStatus
            {
                Provider = ProviderName,
                IsAvailable = false,
                Endpoint = configEndpoint,
                Error = $"Foundry Local not responding on {configEndpoint}. Run 'foundry service set --port {new Uri(configEndpoint).Port}' then 'foundry service start' to fix."
            };
        }

        // No explicit config — full CLI-based discovery via GetStatusAsync
        return await GetStatusAsync();
    }

    public async Task<List<ModelInfo>> GetAvailableModelsAsync()
    {
        var models = new List<ModelInfo>();
        try
        {
            var endpoint = await GetEndpointAsync();
            _logger.LogInformation("Fetching catalog from {Endpoint}/foundry/list", endpoint);
            var response = await _httpClient.GetAsync($"{endpoint}/foundry/list");
            _logger.LogInformation("Catalog response: {Status}", response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Catalog request failed: {Status} — {Body}", response.StatusCode, errBody);
                return models;
            }

            var json = await response.Content.ReadAsStringAsync();
            _logger.LogDebug("Catalog JSON length: {Length}", json.Length);
            using var doc = JsonDocument.Parse(json);

            // Response: { "models": [ { "name": "...", "displayName": "...", "alias": "...", "fileSizeMb": N, ... } ] }
            JsonElement modelsArray;
            if (doc.RootElement.TryGetProperty("models", out modelsArray) && modelsArray.ValueKind == JsonValueKind.Array)
            {
                _catalogCache = new List<JsonElement>();
                _logger.LogInformation("Catalog contains {Count} models", modelsArray.GetArrayLength());
                foreach (var model in modelsArray.EnumerateArray())
                {
                    _catalogCache.Add(model.Clone());

                    var name = model.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var displayName = model.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? name : name;
                    var alias = model.TryGetProperty("alias", out var a) ? a.GetString() : null;

                    long? sizeBytes = null;
                    if (model.TryGetProperty("fileSizeMb", out var fsz) && fsz.ValueKind == JsonValueKind.Number)
                        sizeBytes = (long)(fsz.GetDouble() * 1024 * 1024);

                    var publisher = model.TryGetProperty("publisher", out var pub) ? pub.GetString() : null;
                    var deviceType = "";
                    if (model.TryGetProperty("runtime", out var rt) && rt.TryGetProperty("deviceType", out var dt))
                        deviceType = dt.GetString() ?? "";

                    models.Add(new ModelInfo
                    {
                        Id = alias ?? name,
                        Name = displayName,
                        Description = publisher != null ? $"by {publisher} ({deviceType})" : deviceType,
                        Size = sizeBytes,
                        Status = "available",
                        Provider = ProviderName,
                        Family = model.TryGetProperty("task", out var task) ? task.GetString() : null,
                        ParameterSize = deviceType
                    });
                }
            }
            else
            {
                _logger.LogWarning("Catalog response has no 'models' array. Root kind: {Kind}", doc.RootElement.ValueKind);
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

            // GET /openai/models returns cached (downloaded) models as a string array: ["model-name-1", "model-name-2"]
            var response = await _httpClient.GetAsync($"{endpoint}/openai/models");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    // Check which are currently loaded in memory
                    var loadedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        var loadedResp = await _httpClient.GetAsync($"{endpoint}/openai/loadedmodels");
                        if (loadedResp.IsSuccessStatusCode)
                        {
                            var loadedJson = await loadedResp.Content.ReadAsStringAsync();
                            using var loadedDoc = JsonDocument.Parse(loadedJson);
                            if (loadedDoc.RootElement.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var item in loadedDoc.RootElement.EnumerateArray())
                                {
                                    var val = item.GetString();
                                    if (val != null) loadedSet.Add(val);
                                }
                            }
                        }
                    }
                    catch { }

                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        var modelName = item.ValueKind == JsonValueKind.String
                            ? item.GetString() ?? ""
                            : item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";

                        if (string.IsNullOrEmpty(modelName)) continue;

                        var isLoaded = loadedSet.Contains(modelName);

                        models.Add(new ModelInfo
                        {
                            Id = modelName,
                            Name = modelName,
                            Status = isLoaded ? "loaded" : "downloaded",
                            Provider = ProviderName
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get models from Foundry Local");
        }
        return models;
    }

    public async IAsyncEnumerable<ChatResponse> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var endpoint = await GetEndpointAsync();

        // Load the model first (GET /openai/load/{name})
        try
        {
            _logger.LogInformation("Loading model {Model} at {Endpoint}", request.Model, endpoint);
            var loadResp = await _httpClient.GetAsync($"{endpoint}/openai/load/{Uri.EscapeDataString(request.Model)}", cancellationToken);
            var loadBody = await loadResp.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogInformation("Model load response: {Status} — {Body}", loadResp.StatusCode, loadBody);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Model load request failed for {Model}", request.Model);
        }

        var payload = new
        {
            model = request.Model,
            messages = request.Messages.Select(m => new { role = m.Role, content = m.Content }),
            stream = true,
            temperature = request.Temperature
        };

        var jsonStr = JsonSerializer.Serialize(payload);
        _logger.LogInformation("Chat request to {Endpoint}/v1/chat/completions", endpoint);
        var jsonContent = new StringContent(jsonStr, Encoding.UTF8, "application/json");
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/v1/chat/completions")
        {
            Content = jsonContent
        };

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        _logger.LogInformation("Chat response status: {Status}, Content-Type: {CT}", response.StatusCode, response.Content.Headers.ContentType);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Chat completions returned {Status}: {Body}", response.StatusCode, errorBody);
            yield return new ChatResponse { Content = $"Error from Foundry Local ({response.StatusCode}): {errorBody}", Done = true };
            yield break;
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new System.IO.StreamReader(stream);
        bool receivedAnyContent = false;

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrEmpty(line)) continue;

            _logger.LogDebug("Stream line: {Line}", line);

            // Parse SSE "data: {json}" or raw JSON lines
            string? jsonData = null;
            if (line.StartsWith("data: "))
                jsonData = line["data: ".Length..];
            else if (line.StartsWith("{"))
                jsonData = line;
            else
                continue;

            if (jsonData == "[DONE]")
            {
                if (!receivedAnyContent)
                    yield return new ChatResponse { Content = "(Model returned [DONE] with no content)", Done = true };
                else
                    yield return new ChatResponse { Done = true };
                yield break;
            }

            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(jsonData); }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to parse: {Data} — {Error}", jsonData, ex.Message);
                continue;
            }

            using (doc)
            {
                if (doc.RootElement.TryGetProperty("error", out var errProp))
                {
                    var errMsg = errProp.ValueKind == JsonValueKind.String
                        ? errProp.GetString()
                        : errProp.TryGetProperty("message", out var em) ? em.GetString() : jsonData;
                    yield return new ChatResponse { Content = $"⚠️ {errMsg}", Done = true };
                    yield break;
                }

                if (doc.RootElement.TryGetProperty("choices", out var choices))
                {
                    foreach (var choice in choices.EnumerateArray())
                    {
                        if (choice.TryGetProperty("delta", out var delta) &&
                            delta.TryGetProperty("content", out var content))
                        {
                            var text = content.GetString() ?? "";
                            if (text.Length > 0) receivedAnyContent = true;
                            yield return new ChatResponse { Content = text };
                        }
                        else if (choice.TryGetProperty("message", out var message) &&
                                 message.TryGetProperty("content", out var msgContent))
                        {
                            var text = msgContent.GetString() ?? "";
                            if (text.Length > 0) receivedAnyContent = true;
                            yield return new ChatResponse { Content = text, Done = true };
                        }
                    }
                }
            }
        }

        if (!receivedAnyContent)
        {
            _logger.LogWarning("Chat stream ended with no content for model {Model}", request.Model);
            yield return new ChatResponse { Content = "⚠️ No response from model. Check IIS stdout logs for details.", Done = true };
        }
    }

    public async IAsyncEnumerable<DownloadProgress> DownloadModelAsync(string modelId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new DownloadProgress { ModelId = modelId, Status = "starting" };

        var endpoint = await GetEndpointAsync();

        // Look up the catalog entry to get the URI and metadata needed for the download API
        if (_catalogCache == null)
        {
            // Refresh catalog cache
            await GetAvailableModelsAsync();
        }

        JsonElement? catalogEntry = _catalogCache?.FirstOrDefault(m =>
        {
            var name = m.TryGetProperty("alias", out var a) ? a.GetString() : null;
            name ??= m.TryGetProperty("name", out var n) ? n.GetString() : null;
            return string.Equals(name, modelId, StringComparison.OrdinalIgnoreCase) ||
                   (m.TryGetProperty("name", out var fn) && string.Equals(fn.GetString(), modelId, StringComparison.OrdinalIgnoreCase));
        });

        if (catalogEntry == null || catalogEntry.Value.ValueKind == JsonValueKind.Undefined)
        {
            yield return new DownloadProgress { ModelId = modelId, Status = "error" };
            yield break;
        }

        var entry = catalogEntry.Value;
        var modelName = entry.TryGetProperty("name", out var mn) ? mn.GetString() ?? modelId : modelId;
        var uri = entry.TryGetProperty("uri", out var u) ? u.GetString() ?? "" : "";
        var providerType = entry.TryGetProperty("providerType", out var pt) ? pt.GetString() ?? "AzureFoundryLocal" : "AzureFoundryLocal";

        // Build prompt template from catalog
        var promptTemplate = new Dictionary<string, string>();
        if (entry.TryGetProperty("promptTemplate", out var ptpl))
        {
            if (ptpl.TryGetProperty("assistant", out var ast)) promptTemplate["assistant"] = ast.GetString() ?? "";
            if (ptpl.TryGetProperty("prompt", out var prm)) promptTemplate["prompt"] = prm.GetString() ?? "";
            if (ptpl.TryGetProperty("system", out var sys)) promptTemplate["system"] = sys.GetString() ?? "";
            if (ptpl.TryGetProperty("user", out var usr)) promptTemplate["user"] = usr.GetString() ?? "";
        }

        // Build version-suffixed name
        var version = entry.TryGetProperty("version", out var ver) ? ver.GetString() ?? "" : "";
        var nameWithVersion = !string.IsNullOrEmpty(version) ? $"{modelName}:{version}" : modelName;

        var downloadPayload = new
        {
            model = new
            {
                Uri = uri,
                ProviderType = providerType,
                Name = nameWithVersion,
                Publisher = entry.TryGetProperty("publisher", out var dpub) ? dpub.GetString() ?? "" : "",
                PromptTemplate = promptTemplate
            },
            ignorePipeReport = true
        };

        yield return new DownloadProgress { ModelId = modelId, Status = "downloading" };

        var jsonContent = new StringContent(JsonSerializer.Serialize(downloadPayload), Encoding.UTF8, "application/json");
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/openai/download")
        {
            Content = jsonContent
        };

        HttpResponseMessage? response = null;
        string? sendError = null;
        try
        {
            response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            sendError = ex.Message;
        }

        if (sendError != null || response == null)
        {
            yield return new DownloadProgress { ModelId = modelId, Status = $"error: {sendError}" };
            yield break;
        }

        // The response streams progress as: ("filename", percentage)
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new System.IO.StreamReader(stream);

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrEmpty(line)) continue;

            // Parse streaming progress: ("file.onnx", 0.55) or JSON final response
            var percentMatch = System.Text.RegularExpressions.Regex.Match(line, @",\s*([\d.]+)\)");
            if (percentMatch.Success && double.TryParse(percentMatch.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pct))
            {
                yield return new DownloadProgress
                {
                    ModelId = modelId,
                    Status = "downloading",
                    Percent = Math.Round(pct * 100, 1)
                };
            }

            // Check for final JSON response
            if (line.TrimStart().StartsWith("{"))
            {
                DownloadProgress? finalProgress = null;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("Success", out var success) && success.GetBoolean())
                        finalProgress = new DownloadProgress { ModelId = modelId, Status = "complete", Percent = 100 };
                    else if (doc.RootElement.TryGetProperty("ErrorMessage", out var err))
                        finalProgress = new DownloadProgress { ModelId = modelId, Status = $"error: {err.GetString()}" };
                }
                catch { }

                if (finalProgress != null)
                {
                    yield return finalProgress;
                    yield break;
                }
            }
        }

        yield return new DownloadProgress { ModelId = modelId, Status = "complete", Percent = 100 };
    }
}
