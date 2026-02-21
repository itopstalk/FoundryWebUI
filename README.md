# FoundryWebUI

A web-based chat interface for **Microsoft Foundry Local** and **Ollama**, hosted on IIS. Think of it as a self-hosted [Open WebUI](https://github.com/open-webui/open-webui) alternative built with ASP.NET Core — designed to run on Windows Server alongside your local LLM inference engines.

![Platform](https://img.shields.io/badge/platform-Windows%20Server%202025-blue)
![Framework](https://img.shields.io/badge/.NET-8.0-purple)
![License](https://img.shields.io/badge/license-MIT-green)

## Features

### Phase 1 (Current)

- **💬 Chat Interface** — Conversational UI with streaming responses (Server-Sent Events), message history, and basic Markdown rendering
- **📦 Model Management** — Browse available models, view loaded/downloaded status, and download new models with real-time progress
- **🔌 Dual Provider Support** — Works with both Microsoft Foundry Local and Ollama simultaneously
- **🔍 Auto-Discovery** — Automatically detects the Foundry Local endpoint via CLI; Ollama defaults to `localhost:11434`
- **🌙 Dark Theme** — Bootstrap 5 dark mode UI optimized for extended use
- **🚀 IIS Hosted** — Runs as an in-process IIS application with zero external dependencies beyond .NET

## Architecture

```
Browser ──── HTTP/SSE ────▶ IIS + ASP.NET Core
                                │
                    ┌───────────┴───────────┐
                    ▼                       ▼
             Foundry Local              Ollama
           (dynamic port)         (localhost:11434)
```

| Component | Role |
|---|---|
| **Razor Pages** | Chat (`/`) and Models (`/Models`) pages |
| **API Controller** | REST + SSE endpoints under `/api/` |
| **FoundryLocalService** | Adapter for Foundry Local REST API (`/v1/chat/completions`, `/foundry/list`, etc.) |
| **OllamaService** | Adapter for Ollama REST API (`/api/chat`, `/api/tags`, `/api/pull`) |
| **ILlmProvider** | Shared interface allowing both providers to be used interchangeably |

## Quick Start

### Prerequisites

- Windows Server 2025 (or Windows 10/11)
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Microsoft Foundry Local](https://www.foundrylocal.ai/) and/or [Ollama](https://ollama.com)

### Run locally (development)

```powershell
# Clone or copy the project
cd C:\Projects\FoundryWebUI

# Ensure at least one LLM provider is running
foundry service start        # Foundry Local
# ollama serve               # Ollama (optional)

# Run the app
dotnet run
```

Open `http://localhost:5207` in your browser.

### Deploy to IIS (production)

```powershell
# Use the automated installer (elevated PowerShell)
.\Install-FoundryWebUI.ps1

# Or publish manually
dotnet publish -c Release -o C:\inetpub\FoundryWebUI
```

See [DEPLOYMENT.md](DEPLOYMENT.md) for the full step-by-step guide, including all prerequisite installation commands for a fresh Windows Server 2025.

### Update an existing deployment

After pulling the latest changes from Git, simply re-run the installer:

```powershell
git pull
.\Install-FoundryWebUI.ps1
```

The script auto-detects existing installations and:
- Skips prerequisite installation (IIS, .NET, Foundry, Ollama)
- Stops the IIS site, rebuilds from source, and redeploys
- **Preserves your `appsettings.json` customizations** (e.g., Foundry endpoint overrides)

## API Endpoints

| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/api/status` | Provider health check (returns availability of each provider) |
| `GET` | `/api/models` | List all models across providers (query `?provider=foundry` to filter) |
| `GET` | `/api/models/loaded` | List only currently loaded/ready models |
| `POST` | `/api/chat?provider=foundry` | Streaming chat completion (SSE) |
| `POST` | `/api/models/download` | Download/pull a model with progress (SSE) |

### Chat request example

```json
POST /api/chat?provider=foundry
Content-Type: application/json

{
  "model": "phi-3.5-mini",
  "messages": [
    { "role": "user", "content": "What is Foundry Local?" }
  ],
  "stream": true,
  "temperature": 0.7
}
```

## Configuration

Edit `appsettings.json` (or `appsettings.Production.json` for production overrides):

```json
{
  "LlmProviders": {
    "Foundry": {
      "Endpoint": ""
    },
    "Ollama": {
      "Endpoint": "http://localhost:11434"
    }
  }
}
```

| Setting | Default | Notes |
|---|---|---|
| `Foundry:Endpoint` | *(blank — auto-detect)* | Set explicitly (e.g., `http://localhost:5273`) if auto-detection fails from IIS |
| `Ollama:Endpoint` | `http://localhost:11434` | Change if Ollama runs on a different host/port |

## Project Structure

```
FoundryWebUI/
├── Controllers/
│   └── ApiController.cs          # REST + SSE API endpoints
├── Models/
│   └── LlmModels.cs              # DTOs (ChatMessage, ModelInfo, etc.)
├── Services/
│   ├── ILlmProvider.cs           # Provider interface
│   ├── FoundryLocalService.cs    # Foundry Local adapter
│   └── OllamaService.cs         # Ollama adapter
├── Pages/
│   ├── Index.cshtml              # Chat page
│   ├── Models.cshtml             # Model management page
│   └── Shared/_Layout.cshtml     # Shared layout (dark theme)
├── wwwroot/
│   ├── css/site.css              # Custom styles
│   └── js/
│       ├── site.js               # Provider status check
│       ├── chat.js               # Chat UI logic + SSE streaming
│       └── models.js             # Model listing + download progress
├── Program.cs                    # App startup and DI configuration
├── appsettings.json              # Configuration
├── web.config                    # IIS hosting configuration
├── Install-FoundryWebUI.ps1      # Automated deployment script
└── DEPLOYMENT.md                 # Full deployment & troubleshooting guide
```

## Troubleshooting

| Symptom | Likely Cause | Fix |
|---|---|---|
| Provider shows red ✗ in nav bar | Provider not running or unreachable | Start the provider (`foundry service start` / `ollama serve`) |
| No models listed | No models downloaded yet | Go to Models page and download one, or use CLI (`foundry model run phi-3.5-mini`) |
| HTTP 500.30 on IIS | Hosting Bundle not installed or misconfigured | See [DEPLOYMENT.md — Troubleshooting](DEPLOYMENT.md#troubleshooting) |
| Chat shows no streaming | Reverse proxy buffering SSE | Add `X-Accel-Buffering: no` header; see DEPLOYMENT.md |
| Can't access from other machines | Windows Firewall blocking port | `New-NetFirewallRule -DisplayName "FoundryWebUI" -Direction Inbound -Protocol TCP -LocalPort 80 -Action Allow` |

For the complete troubleshooting guide with 9 scenarios and solutions, see [DEPLOYMENT.md](DEPLOYMENT.md#troubleshooting).

## Roadmap

- [ ] **Phase 2**: Conversation persistence (save/load chat history)
- [ ] **Phase 2**: System prompt customization
- [ ] **Phase 2**: Model parameter tuning (temperature, top_p, max_tokens)
- [ ] **Phase 3**: Multi-user support with session isolation
- [ ] **Phase 3**: File upload and document Q&A
- [ ] **Phase 3**: RAG (Retrieval-Augmented Generation) integration

## License

MIT
