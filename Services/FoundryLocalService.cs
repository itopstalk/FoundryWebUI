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
                        Id = alias ?? name,
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

        // Use the Foundry CLI to download — the HTTP API (/openai/download) is unreliable
        var foundryPath = FindFoundryExecutable();
        if (foundryPath == null)
        {
            yield return new DownloadProgress { ModelId = modelId, Status = "error: foundry CLI not found on system" };
            yield break;
        }

        _logger.LogInformation("Starting download of {Model} using CLI: {Foundry}", modelId, foundryPath);
        yield return new DownloadProgress { ModelId = modelId, Status = "downloading" };

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = foundryPath,
            Arguments = $"model download \"{modelId}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        System.Diagnostics.Process? process = null;
        string? startError = null;
        try
        {
            process = System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            startError = ex.Message;
        }

        if (process == null || startError != null)
        {
            yield return new DownloadProgress { ModelId = modelId, Status = $"error: failed to start foundry CLI — {startError}" };
            yield break;
        }

        // Poll until the process exits, sending heartbeats
        var started = DateTime.UtcNow;
        while (!process.HasExited && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(3000, cancellationToken);
            var elapsed = (DateTime.UtcNow - started).TotalSeconds;
            yield return new DownloadProgress
            {
                ModelId = modelId,
                Status = $"downloading ({TimeSpan.FromSeconds(elapsed):mm\\:ss} elapsed)",
                Percent = null
            };
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        _logger.LogInformation("Foundry download exit code: {Code}, stdout: {Out}, stderr: {Err}",
            process.ExitCode,
            stdout.Length > 500 ? stdout[..500] : stdout,
            stderr.Length > 500 ? stderr[..500] : stderr);

        if (process.ExitCode == 0)
        {
            yield return new DownloadProgress { ModelId = modelId, Status = "complete", Percent = 100 };
        }
        else
        {
            var errMsg = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : stdout.Trim();
            yield return new DownloadProgress { ModelId = modelId, Status = $"error: {errMsg}" };
        }
    }

    public async Task<bool> DeleteModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Deleting model {ModelId}", modelId);
        var foundryPath = FindFoundryExecutable();
        if (foundryPath == null)
        {
            _logger.LogError("foundry CLI not found — cannot delete model");
            return false;
        }

        try
        {
            // Foundry uses "cache remove" not "model remove"
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = foundryPath,
                Arguments = $"cache remove \"{modelId}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = System.Diagnostics.Process.Start(psi);
            if (process != null)
            {
                // Auto-confirm any prompt
                await process.StandardInput.WriteLineAsync("y");
                process.StandardInput.Close();

                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                if (process.WaitForExit(60000))
                {
                    var output = await outputTask;
                    var error = await errorTask;
                    _logger.LogInformation("Cache remove exit: {Code}, stdout: {Out}, stderr: {Err}", process.ExitCode, output, error);
                    return process.ExitCode == 0;
                }
                else
                {
                    try { process.Kill(); } catch { }
                    _logger.LogWarning("Cache remove timed out for {ModelId}", modelId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete model {ModelId}", modelId);
        }
        return false;
    }

    /// <summary>Find the foundry executable — IIS app pool may not have it on PATH.</summary>
    private string? FindFoundryExecutable()
    {
        // 1. Check appsettings.json config
        var configPath = _configuration["LlmProviders:Foundry:CliPath"];
        if (!string.IsNullOrEmpty(configPath) && File.Exists(configPath))
        {
            _logger.LogInformation("Using configured foundry path: {Path}", configPath);
            return configPath;
        }
        // Check each user profile for common foundry locations
        var usersDir = @"C:\Users";
        if (Directory.Exists(usersDir))
        {
            foreach (var userDir in Directory.GetDirectories(usersDir))
            {
                // winget installs create App Execution Aliases here
                var wingetAlias = Path.Combine(userDir, "AppData", "Local", "Microsoft", "WinGet", "Links", "foundry.exe");
                if (File.Exists(wingetAlias))
                {
                    _logger.LogInformation("Found foundry at {Path}", wingetAlias);
                    return wingetAlias;
                }

                // WindowsApps execution alias
                var windowsApps = Path.Combine(userDir, "AppData", "Local", "Microsoft", "WindowsApps", "foundry.exe");
                if (File.Exists(windowsApps))
                {
                    _logger.LogInformation("Found foundry at {Path}", windowsApps);
                    return windowsApps;
                }

                // .foundry SDK install
                var userFoundry = Path.Combine(userDir, ".foundry", "bin", "foundry.exe");
                if (File.Exists(userFoundry))
                {
                    _logger.LogInformation("Found foundry at {Path}", userFoundry);
                    return userFoundry;
                }
            }
        }

        // Check common install locations
        var candidates = new[]
        {
            @"C:\Program Files\Foundry\foundry.exe",
            @"C:\Program Files (x86)\Foundry\foundry.exe",
            @"C:\Program Files\WindowsApps\foundry.exe",
        };
        foreach (var path in candidates)
        {
            if (File.Exists(path)) return path;
        }

        // Try PATH via 'where' command as fallback
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "where",
                Arguments = "foundry",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc != null && proc.WaitForExit(5000) && proc.ExitCode == 0)
            {
                var result = proc.StandardOutput.ReadToEnd().Trim().Split('\n')[0].Trim();
                if (File.Exists(result)) return result;
            }
        }
        catch { }

        return null;
    }
}
