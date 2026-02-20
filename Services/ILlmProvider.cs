using FoundryWebUI.Models;

namespace FoundryWebUI.Services;

public interface ILlmProvider
{
    string ProviderName { get; }
    Task<ProviderStatus> GetStatusAsync();
    Task<List<ModelInfo>> GetAvailableModelsAsync();
    Task<List<ModelInfo>> GetLoadedModelsAsync();
    IAsyncEnumerable<ChatResponse> StreamChatAsync(ChatRequest request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<DownloadProgress> DownloadModelAsync(string modelId, CancellationToken cancellationToken = default);
}
