namespace FoundryWebUI.Models;

public class ChatMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
}

public class ChatRequest
{
    public string Model { get; set; } = string.Empty;
    public List<ChatMessage> Messages { get; set; } = new();
    public bool Stream { get; set; } = true;
    public double Temperature { get; set; } = 0.7;
}

public class ChatResponse
{
    public string Content { get; set; } = string.Empty;
    public bool Done { get; set; }
    public string? Error { get; set; }
}

public class ModelInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public long? Size { get; set; }
    public string? Status { get; set; } // "available", "downloaded", "loaded"
    public string Provider { get; set; } = string.Empty; // "foundry" or "ollama"
    public string? ParameterSize { get; set; }
    public string? Family { get; set; }
    public double? EstimatedRamMb { get; set; }
}

public class DownloadRequest
{
    public string ModelId { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
}

public class DownloadProgress
{
    public string ModelId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public long? Total { get; set; }
    public long? Completed { get; set; }
    public double? Percent { get; set; }
}

public class ProviderStatus
{
    public string Provider { get; set; } = string.Empty;
    public bool IsAvailable { get; set; }
    public string? Endpoint { get; set; }
    public string? Error { get; set; }
}
