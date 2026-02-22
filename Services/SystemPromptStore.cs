using System.Text.Json;

namespace FoundryWebUI.Services;

public class SystemPrompt
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class SystemPromptStore
{
    private readonly string _filePath;
    private readonly ILogger<SystemPromptStore> _logger;
    private List<SystemPrompt> _prompts = new();
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public SystemPromptStore(IWebHostEnvironment env, ILogger<SystemPromptStore> logger)
    {
        _logger = logger;
        _filePath = Path.Combine(env.ContentRootPath, "system-prompts.json");
        Load();
    }

    private void Load()
    {
        lock (_lock)
        {
            if (File.Exists(_filePath))
            {
                try
                {
                    var json = File.ReadAllText(_filePath);
                    _prompts = JsonSerializer.Deserialize<List<SystemPrompt>>(json, _jsonOptions) ?? new();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to load system prompts from {Path}", _filePath);
                    _prompts = new();
                }
            }

            // Ensure there's always a default prompt
            if (_prompts.Count == 0)
            {
                _prompts.Add(new SystemPrompt
                {
                    Id = "default",
                    Name = "Default",
                    Content = "You are a helpful AI assistant.",
                    IsDefault = true
                });
                Save();
            }

            // Ensure exactly one default
            if (!_prompts.Any(p => p.IsDefault))
            {
                _prompts[0].IsDefault = true;
                Save();
            }
        }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_prompts, _jsonOptions);
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save system prompts to {Path}", _filePath);
        }
    }

    public List<SystemPrompt> GetAll()
    {
        lock (_lock) { return _prompts.ToList(); }
    }

    public SystemPrompt? GetById(string id)
    {
        lock (_lock) { return _prompts.FirstOrDefault(p => p.Id == id); }
    }

    public SystemPrompt? GetDefault()
    {
        lock (_lock) { return _prompts.FirstOrDefault(p => p.IsDefault) ?? _prompts.FirstOrDefault(); }
    }

    public SystemPrompt Add(string name, string content)
    {
        lock (_lock)
        {
            var prompt = new SystemPrompt
            {
                Name = name,
                Content = content,
                IsDefault = _prompts.Count == 0
            };
            _prompts.Add(prompt);
            Save();
            return prompt;
        }
    }

    public SystemPrompt? Update(string id, string name, string content)
    {
        lock (_lock)
        {
            var prompt = _prompts.FirstOrDefault(p => p.Id == id);
            if (prompt == null) return null;
            prompt.Name = name;
            prompt.Content = content;
            prompt.UpdatedAt = DateTime.UtcNow;
            Save();
            return prompt;
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var prompt = _prompts.FirstOrDefault(p => p.Id == id);
            if (prompt == null) return false;
            var wasDefault = prompt.IsDefault;
            _prompts.Remove(prompt);
            if (wasDefault && _prompts.Count > 0)
                _prompts[0].IsDefault = true;
            Save();
            return true;
        }
    }

    public bool SetDefault(string id)
    {
        lock (_lock)
        {
            var prompt = _prompts.FirstOrDefault(p => p.Id == id);
            if (prompt == null) return false;
            foreach (var p in _prompts) p.IsDefault = false;
            prompt.IsDefault = true;
            Save();
            return true;
        }
    }
}
