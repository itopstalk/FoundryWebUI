using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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

        // Probe common ports to discover Foundry Local (no CLI needed)
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

            // Response is either a plain array [...] or { "models": [...] }
            JsonElement modelsArray;
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                modelsArray = doc.RootElement;
            }
            else if (doc.RootElement.TryGetProperty("models", out var nested) && nested.ValueKind == JsonValueKind.Array)
            {
                modelsArray = nested;
            }
            else
            {
                _logger.LogWarning("Catalog response has unexpected format. Root kind: {Kind}", doc.RootElement.ValueKind);
                return models;
            }
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
                    double? fileSizeMb = null;
                    if (model.TryGetProperty("fileSizeMb", out var fsz) && fsz.ValueKind == JsonValueKind.Number)
                    {
                        fileSizeMb = fsz.GetDouble();
                        sizeBytes = (long)(fileSizeMb.Value * 1024 * 1024);
                    }

                    // Estimated RAM: ~1.2x file size (model weights + KV cache + runtime overhead)
                    double? estimatedRamMb = fileSizeMb.HasValue ? Math.Round(fileSizeMb.Value * 1.2, 0) : null;

                    var publisher = model.TryGetProperty("publisher", out var pub) ? pub.GetString() : null;
                    var deviceType = "";
                    if (model.TryGetProperty("runtime", out var rt) && rt.TryGetProperty("deviceType", out var dt))
                        deviceType = dt.GetString() ?? "";

                    models.Add(new ModelInfo
                    {
                        Id = name,
                        Name = displayName,
                        Description = publisher != null ? $"by {publisher} ({deviceType})" : deviceType,
                        Size = sizeBytes,
                        EstimatedRamMb = estimatedRamMb,
                        Status = "available",
                        Provider = ProviderName,
                        Family = model.TryGetProperty("task", out var task) ? task.GetString() : null,
                        ParameterSize = deviceType
                    });
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

        // Find the catalog entry for this model to build the correct download request
        if (_catalogCache == null)
        {
            // Force a catalog fetch if not cached
            await GetAvailableModelsAsync();
        }

        JsonElement? catalogEntry = null;
        if (_catalogCache != null)
        {
            foreach (var entry in _catalogCache)
            {
                var alias = entry.TryGetProperty("alias", out var a) ? a.GetString() : null;
                var name = entry.TryGetProperty("name", out var n) ? n.GetString() : null;
                var displayName = entry.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;
                if (string.Equals(alias, modelId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, modelId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(displayName, modelId, StringComparison.OrdinalIgnoreCase))
                {
                    catalogEntry = entry;
                    break;
                }
            }
        }

        if (catalogEntry == null)
        {
            yield return new DownloadProgress { ModelId = modelId, Status = $"error: model '{modelId}' not found in catalog" };
            yield break;
        }

        var cat = catalogEntry.Value;
        var modelUri = cat.TryGetProperty("uri", out var u) ? u.GetString() ?? "" : "";
        var modelName = cat.TryGetProperty("name", out var mn) ? mn.GetString() ?? modelId : modelId;
        // Always use "AzureFoundryLocal" — the catalog returns "AzureFoundry" which doesn't work with the download API
        var providerType = "AzureFoundryLocal";
        var publisher = cat.TryGetProperty("publisher", out var pub) ? pub.GetString() ?? "" : "";

        // Build the download request body per official Foundry Local REST API docs
        var downloadBody = new
        {
            model = new
            {
                Uri = modelUri,
                Name = modelName,
                ProviderType = providerType,
                Publisher = publisher
            },
            ignorePipeReport = true
        };

        var jsonBody = JsonSerializer.Serialize(downloadBody);
        _logger.LogInformation("Starting REST download of {Model}: {Body}", modelId, jsonBody);
        yield return new DownloadProgress { ModelId = modelId, Status = "downloading", Percent = 0 };

        // Use a Channel to bridge the try/catch async work with yield return
        var channel = System.Threading.Channels.Channel.CreateUnbounded<DownloadProgress>();

        // Start the download reader in a background task
        _ = Task.Run(async () =>
        {
            HttpResponseMessage? response = null;
            try
            {
                var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/openai/download")
                {
                    Content = content
                };
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var errBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    await channel.Writer.WriteAsync(new DownloadProgress { ModelId = modelId, Status = $"error: HTTP {response.StatusCode} — {errBody}" });
                    return;
                }

                var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var reader = new StreamReader(stream);
                var buffer = new char[4096];
                var lineBuffer = new StringBuilder();
                double lastPercent = 0;
                var started = DateTime.UtcNow;

                while (!cancellationToken.IsCancellationRequested)
                {
                    int read = await reader.ReadAsync(buffer, 0, buffer.Length);
                    if (read == 0) break;

                    lineBuffer.Append(buffer, 0, read);
                    var text = lineBuffer.ToString();

                    // Parse progress lines: "Total X.XXX% Downloading filename"
                    var matches = Regex.Matches(text, @"Total\s+([\d.]+)%");
                    if (matches.Count > 0)
                    {
                        var latestMatch = matches[^1];
                        if (double.TryParse(latestMatch.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var percent))
                        {
                            lastPercent = percent;
                            var elapsed = (DateTime.UtcNow - started).TotalSeconds;
                            await channel.Writer.WriteAsync(new DownloadProgress
                            {
                                ModelId = modelId,
                                Status = $"downloading ({TimeSpan.FromSeconds(elapsed):mm\\:ss} elapsed)",
                                Percent = percent
                            });
                        }
                    }

                    // Check for final JSON response
                    if (text.Contains("\"success\"") || text.Contains("\"Success\""))
                    {
                        var jsonStart = text.IndexOf('{');
                        if (jsonStart >= 0)
                        {
                            var jsonStr = text[jsonStart..];
                            try
                            {
                                using var doc = JsonDocument.Parse(jsonStr);
                                var success = false;
                                if (doc.RootElement.TryGetProperty("success", out var s))
                                    success = s.GetBoolean();
                                else if (doc.RootElement.TryGetProperty("Success", out var s2))
                                    success = s2.GetBoolean();

                                if (success)
                                    await channel.Writer.WriteAsync(new DownloadProgress { ModelId = modelId, Status = "complete", Percent = 100 });
                                else
                                {
                                    var errMsg = doc.RootElement.TryGetProperty("errorMessage", out var e) ? e.GetString()
                                        : doc.RootElement.TryGetProperty("ErrorMessage", out var e2) ? e2.GetString()
                                        : "Unknown error";
                                    await channel.Writer.WriteAsync(new DownloadProgress { ModelId = modelId, Status = $"error: {errMsg}" });
                                }
                                return;
                            }
                            catch { /* partial JSON, keep reading */ }
                        }
                    }

                    var lastNewline = text.LastIndexOf('\n');
                    if (lastNewline >= 0)
                        lineBuffer = new StringBuilder(text[(lastNewline + 1)..]);
                }

                // Stream ended — determine outcome
                if (lastPercent >= 99)
                    await channel.Writer.WriteAsync(new DownloadProgress { ModelId = modelId, Status = "complete", Percent = 100 });
                else
                    await channel.Writer.WriteAsync(new DownloadProgress { ModelId = modelId, Status = $"error: download stream ended at {lastPercent:F1}%" });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Download stream error");
                await channel.Writer.WriteAsync(new DownloadProgress { ModelId = modelId, Status = $"error: {ex.Message}" });
            }
            finally
            {
                response?.Dispose();
                channel.Writer.Complete();
            }
        }, cancellationToken);

        await foreach (var progress in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return progress;
        }
    }

    public async Task<bool> DeleteModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Deleting model {ModelId} via REST + file deletion", modelId);
        var endpoint = await GetEndpointAsync();

        // Step 1: Unload the model from memory via REST
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(10000);
            var unloadResp = await _httpClient.GetAsync($"{endpoint}/openai/unload/{Uri.EscapeDataString(modelId)}?force=true", cts.Token);
            _logger.LogInformation("Unload {ModelId}: {Status}", modelId, unloadResp.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unload request failed for {ModelId} (may not be loaded)", modelId);
        }

        // Step 2: Get cache directory from /openai/status
        string? modelDirPath = null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(5000);
            var statusResp = await _httpClient.GetAsync($"{endpoint}/openai/status", cts.Token);
            if (statusResp.IsSuccessStatusCode)
            {
                var statusJson = await statusResp.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(statusJson);
                // Try both camelCase and PascalCase
                if (doc.RootElement.TryGetProperty("modelDirPath", out var mdp))
                    modelDirPath = mdp.GetString();
                else if (doc.RootElement.TryGetProperty("ModelDirPath", out var mdp2))
                    modelDirPath = mdp2.GetString();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get model directory path from status endpoint");
        }

        if (string.IsNullOrEmpty(modelDirPath) || !Directory.Exists(modelDirPath))
        {
            _logger.LogError("Cannot determine model cache directory (modelDirPath={Path}, exists={Exists})", 
                modelDirPath, modelDirPath != null && Directory.Exists(modelDirPath));
            return false;
        }

        _logger.LogInformation("Model cache directory: {Path}", modelDirPath);

        // Step 3: Find and delete the model directory
        // Model directories follow pattern: {modelDirPath}/{Publisher}/{ModelName-Version}
        // where ":" in the model ID is replaced with "-" in the directory name
        var dirName = modelId.Replace(':', '-');
        bool deleted = false;

        // Log what's actually in the cache directory for debugging
        try
        {
            foreach (var pubDir in Directory.GetDirectories(modelDirPath))
            {
                _logger.LogInformation("  Publisher dir: {Dir}", pubDir);
                foreach (var modelDir2 in Directory.GetDirectories(pubDir))
                {
                    _logger.LogInformation("    Model dir: {Dir}", Path.GetFileName(modelDir2));
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Permission denied accessing cache directory {Path}. Grant the IIS app pool identity access: icacls \"{Path}\" /grant \"IIS AppPool\\FoundryLocalWebUI:(OI)(CI)F\" /T", modelDirPath, modelDirPath);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cannot enumerate cache directory (permission issue?)");
        }

        _logger.LogInformation("Looking for directory named '{DirName}' in publisher subdirectories", dirName);

        foreach (var publisherDir in Directory.GetDirectories(modelDirPath))
        {
            var modelDir = Path.Combine(publisherDir, dirName);
            _logger.LogInformation("Checking: {Path} (exists={Exists})", modelDir, Directory.Exists(modelDir));
            if (Directory.Exists(modelDir))
            {
                try
                {
                    Directory.Delete(modelDir, recursive: true);
                    _logger.LogInformation("Deleted model directory: {Path}", modelDir);
                    deleted = true;
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete model directory {Path}", modelDir);
                    return false;
                }
            }
        }

        if (!deleted)
        {
            // Try finding by partial name match in case the directory naming is different
            foreach (var publisherDir in Directory.GetDirectories(modelDirPath))
            {
                var nameWithoutVersion = modelId.Contains(':') ? modelId[..modelId.LastIndexOf(':')] : modelId;
                foreach (var dir in Directory.GetDirectories(publisherDir))
                {
                    var folderName = Path.GetFileName(dir);
                    if (folderName.StartsWith(nameWithoutVersion.Replace(':', '-'), StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            Directory.Delete(dir, recursive: true);
                            _logger.LogInformation("Deleted model directory (partial match): {Path}", dir);
                            deleted = true;
                            break;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to delete model directory {Path}", dir);
                            return false;
                        }
                    }
                }
                if (deleted) break;
            }
        }

        if (!deleted)
        {
            _logger.LogWarning("Could not find model directory for {ModelId} in {CachePath}", modelId, modelDirPath);
            return false;
        }

        return true;
    }

}
