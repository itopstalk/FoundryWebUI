using Microsoft.AspNetCore.Mvc;
using FoundryWebUI.Models;
using FoundryWebUI.Services;
using System.Text.Json;

namespace FoundryWebUI.Controllers;

[ApiController]
[Route("api")]
public class ApiController : ControllerBase
{
    private readonly IEnumerable<ILlmProvider> _providers;
    private readonly ILogger<ApiController> _logger;
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ApiController(IEnumerable<ILlmProvider> providers, ILogger<ApiController> logger)
    {
        _providers = providers;
        _logger = logger;
    }

    private ILlmProvider? GetProvider(string provider)
    {
        return _providers.FirstOrDefault(p => p.ProviderName.Equals(provider, StringComparison.OrdinalIgnoreCase));
    }

    [HttpGet("system-info")]
    public IActionResult GetSystemInfo()
    {
        var gcInfo = GC.GetGCMemoryInfo();
        var totalRamBytes = gcInfo.TotalAvailableMemoryBytes;
        var totalRamMb = totalRamBytes / (1024.0 * 1024.0);
        var totalRamGb = totalRamMb / 1024.0;
        return Ok(new { totalRamMb = Math.Round(totalRamMb, 0), totalRamGb = Math.Round(totalRamGb, 1) });
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus()
    {
        var tasks = _providers.Select(p => p.GetStatusAsync());
        var statuses = await Task.WhenAll(tasks);
        return Ok(statuses);
    }

    [HttpPost("reconnect")]
    public async Task<IActionResult> Reconnect([FromQuery] string provider = "foundry")
    {
        var p = GetProvider(provider);
        if (p == null)
            return NotFound(new { error = $"Provider '{provider}' not found" });

        var status = await p.ReconnectAsync();
        return Ok(status);
    }

    [HttpGet("models")]
    public async Task<IActionResult> GetModels([FromQuery] string? provider = null)
    {
        var allModels = new List<ModelInfo>();

        var providers = provider != null
            ? _providers.Where(p => p.ProviderName.Equals(provider, StringComparison.OrdinalIgnoreCase))
            : _providers;

        foreach (var p in providers)
        {
            try
            {
                var loaded = await p.GetLoadedModelsAsync();
                var available = await p.GetAvailableModelsAsync();

                // Merge: loaded/downloaded models take priority, then add catalog entries not yet downloaded
                // Enrich downloaded models with catalog metadata (size, RAM estimates)
                // Build lookups by both alias (Id) and full name for matching
                var catalogById = new Dictionary<string, ModelInfo>(StringComparer.OrdinalIgnoreCase);
                var catalogByName = new Dictionary<string, ModelInfo>(StringComparer.OrdinalIgnoreCase);
                foreach (var m in available)
                {
                    catalogById.TryAdd(m.Id, m);
                    // The full catalog name (e.g., "Phi-3-mini-4k-instruct-generic-cpu:2") may match downloaded model IDs
                    // Store a mapping from display name patterns too
                }
                // Build name-based lookup from the raw catalog for matching downloaded model IDs
                // Downloaded models use full names like "Phi-3-mini-4k-instruct-generic-cpu:2"
                // Catalog entries use aliases like "phi-3-mini-4k" as Id
                // We need to match by checking if downloaded ID contains or equals catalog name patterns
                var loadedIds = new HashSet<string>(loaded.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
                foreach (var m in loaded)
                {
                    ModelInfo? catModel = null;
                    // Try direct match first
                    if (!catalogById.TryGetValue(m.Id, out catModel))
                    {
                        // Downloaded IDs are like "Phi-3-mini-4k-instruct-generic-cpu:2" (name:version)
                        // Catalog Names are like "Phi-3-mini-4k-instruct-generic-cpu" (displayName)
                        var idWithoutVersion = m.Id.Contains(':') ? m.Id[..m.Id.LastIndexOf(':')] : m.Id;
                        catModel = available.FirstOrDefault(a =>
                            string.Equals(a.Name, idWithoutVersion, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(a.Name, m.Id, StringComparison.OrdinalIgnoreCase));
                    }
                    if (catModel != null)
                    {
                        m.Size ??= catModel.Size;
                        m.EstimatedRamMb ??= catModel.EstimatedRamMb;
                        m.Description ??= catModel.Description;
                        m.Family ??= catModel.Family;
                        m.ParameterSize ??= catModel.ParameterSize;
                        if (string.IsNullOrEmpty(m.Name) || m.Name == m.Id)
                            m.Name = catModel.Name;
                    }
                }
                allModels.AddRange(loaded);
                allModels.AddRange(available.Where(m => !loadedIds.Contains(m.Id)));
                _logger.LogInformation("Models: {Loaded} loaded/downloaded, {Available} in catalog, {Total} total after merge",
                    loaded.Count, available.Count, allModels.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get models from {Provider}", p.ProviderName);
            }
        }

        return Ok(allModels);
    }

    [HttpGet("models/loaded")]
    public async Task<IActionResult> GetLoadedModels()
    {
        var allModels = new List<ModelInfo>();
        foreach (var p in _providers)
        {
            try
            {
                allModels.AddRange(await p.GetLoadedModelsAsync());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get loaded models from {Provider}", p.ProviderName);
            }
        }
        return Ok(allModels);
    }

    [HttpPost("chat")]
    public async Task Chat([FromBody] ChatRequest request, [FromQuery] string provider = "foundry")
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        var p = GetProvider(provider);
        if (p == null)
        {
            await WriteSSE("message", JsonSerializer.Serialize(new { content = $"⚠️ Provider '{provider}' not found", done = true }));
            return;
        }

        try
        {
            await foreach (var chunk in p.StreamChatAsync(request, HttpContext.RequestAborted))
            {
                var json = JsonSerializer.Serialize(chunk, _jsonOptions);
                await WriteSSE("message", json);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chat error with provider {Provider}", provider);
            await WriteSSE("message", JsonSerializer.Serialize(new { content = $"\n\n⚠️ Error: {ex.Message}", done = true }));
        }
    }

    [HttpPost("models/download")]
    public async Task DownloadModel([FromBody] DownloadRequest request)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";
        Response.Headers["X-Accel-Buffering"] = "no";

        var p = GetProvider(request.Provider);
        if (p == null)
        {
            await WriteSSE("error", JsonSerializer.Serialize(new { error = $"Provider '{request.Provider}' not found" }));
            return;
        }

        try
        {
            await foreach (var progress in p.DownloadModelAsync(request.ModelId, HttpContext.RequestAborted))
            {
                var json = JsonSerializer.Serialize(progress, _jsonOptions);
                await WriteSSE("progress", json);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download error");
            await WriteSSE("error", JsonSerializer.Serialize(new { error = ex.Message }));
        }
    }

    [HttpDelete("models/{modelId}")]
    public async Task<IActionResult> DeleteModel(string modelId, [FromQuery] string provider = "foundry")
    {
        var p = GetProvider(provider);
        if (p == null)
            return NotFound(new { error = $"Provider '{provider}' not found" });

        var success = await p.DeleteModelAsync(modelId, HttpContext.RequestAborted);
        if (success)
            return Ok(new { message = $"Model '{modelId}' removed successfully" });
        else
            return StatusCode(500, new { error = $"Failed to remove model '{modelId}'. Check server logs." });
    }

    private async Task WriteSSE(string eventType, string data)
    {
        await Response.WriteAsync($"event: {eventType}\ndata: {data}\n\n");
        await Response.Body.FlushAsync();
    }
}
