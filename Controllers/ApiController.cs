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

    public ApiController(IEnumerable<ILlmProvider> providers, ILogger<ApiController> logger)
    {
        _providers = providers;
        _logger = logger;
    }

    private ILlmProvider? GetProvider(string provider)
    {
        return _providers.FirstOrDefault(p => p.ProviderName.Equals(provider, StringComparison.OrdinalIgnoreCase));
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

                // Merge: mark loaded models, add available ones not yet loaded
                var loadedIds = loaded.Select(m => m.Id).ToHashSet();
                allModels.AddRange(loaded);
                allModels.AddRange(available.Where(m => !loadedIds.Contains(m.Id)));
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
            await WriteSSE("error", JsonSerializer.Serialize(new { error = $"Provider '{provider}' not found" }));
            return;
        }

        try
        {
            await foreach (var chunk in p.StreamChatAsync(request, HttpContext.RequestAborted))
            {
                var json = JsonSerializer.Serialize(chunk);
                await WriteSSE("message", json);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chat error");
            await WriteSSE("error", JsonSerializer.Serialize(new { error = ex.Message }));
        }
    }

    [HttpPost("models/download")]
    public async Task DownloadModel([FromBody] DownloadRequest request)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

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
                var json = JsonSerializer.Serialize(progress);
                await WriteSSE("progress", json);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download error");
            await WriteSSE("error", JsonSerializer.Serialize(new { error = ex.Message }));
        }
    }

    private async Task WriteSSE(string eventType, string data)
    {
        await Response.WriteAsync($"event: {eventType}\ndata: {data}\n\n");
        await Response.Body.FlushAsync();
    }
}
