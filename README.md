# FoundryLocalWebUI

A web-based chat interface for **Microsoft Foundry Local**, hosted on IIS. Think of it as a self-hosted [Open WebUI](https://github.com/open-webui/open-webui) alternative built with ASP.NET Core -- designed to run on Windows Server alongside your local LLM inference engine.

> **Note**: This project supports **Foundry Local only**. Ollama is not supported.

![Platform](https://img.shields.io/badge/platform-Windows%20Server%202025-blue)
![Framework](https://img.shields.io/badge/.NET-8.0-purple)
![License](https://img.shields.io/badge/license-MIT-green)

[![Watch the video](https://img.youtube.com/vi/IGWNhSQziZI/maxresdefault.jpg)](https://youtu.be/IGWNhSQziZI)

## Deployment

Deployment to a fresh Windows Server requires only **Git** and an elevated PowerShell prompt. The installer script handles everything else.

### Step 1: Install Git

```powershell
winget install --id Git.Git -e --accept-source-agreements --accept-package-agreements
# Close and reopen PowerShell so git is on PATH
```

### Step 2: Clone and run the installer

```powershell
cd C:\Projects
git clone https://github.com/itopstalk/FoundryWebUI.git FoundryLocalWebUI
cd FoundryLocalWebUI
.\Install-FoundryWebUI.ps1
```
You will need to run the following to run unsigned scripts (remember to reenable later)

```powershell
Set-ExecutionPolicy unrestricted
```


### What the installer does

On **first run**, the script performs the following (all automated, no manual steps):

- **Checks for WinGet** -- required for installing other components
- **Installs IIS** with required features (WebSockets, static compression, Windows Authentication)
- **Installs .NET 8.0 Hosting Bundle** -- required for IIS to run ASP.NET Core apps
- **Installs .NET 8.0 SDK** -- required to build the application from source
- **Installs Microsoft Foundry Local** via WinGet and pins it to port 5273
- **Builds the application** from source using `dotnet publish`
- **Creates an IIS website** and application pool (defaults: site `FoundryLocalWebUI` on port 80)
- **Configures permissions** -- grants IIS app pool identity access to the Foundry Local model cache
- **Adds a Windows Firewall rule** for the configured port
- **Verifies the deployment** by testing the site URL and API endpoint

On **subsequent runs** (after `git pull`), the script auto-detects the existing installation and:

- Skips prerequisite installation (IIS, .NET, Foundry Local)
- Stops the IIS site, rebuilds from source, and redeploys
- **Preserves your `appsettings.json` customizations** (e.g., Foundry endpoint)

### Update an existing deployment

```powershell
cd C:\Projects\FoundryLocalWebUI
git pull
.\Install-FoundryWebUI.ps1
```

See [DEPLOYMENT.md](DEPLOYMENT.md) for the full step-by-step manual guide and troubleshooting.

## Features

- **Chat Interface** -- Conversational UI with streaming responses (Server-Sent Events), message history, and basic Markdown rendering
- **Model Management** -- Browse the full Foundry Local catalog (40+ models), download with progress tracking, and remove downloaded models
- **Sortable Model Table** -- Click any column header to sort by name, status, size, RAM, device type, etc.
- **Can Run Indicator** -- Estimates RAM requirements for each model and shows whether your system can run it
- **Foundry Local Connection** -- Bright green/red status indicator with endpoint display and reconnect button
- **Auto-Discovery** -- Automatically detects the Foundry Local endpoint via port scanning
- **REST-Only** -- No CLI dependency; all interactions (download, delete, chat) use Foundry Local REST APIs
- **Logs Page** -- View application, IIS, Foundry Local, and Windows Event Log entries with filtering and search
- **Sidebar Navigation** -- Collapsible sidebar for navigating between Chat, Models, and Logs pages
- **Dark Theme** -- Bootstrap 5 dark mode UI optimized for extended use
- **IIS Hosted** -- Runs as an in-process IIS application with zero external dependencies beyond .NET

## Architecture

```
Browser ──── HTTP/SSE ────▶ IIS + ASP.NET Core
                                │
                                ▼
                         Foundry Local
                        (port 5273)
```

| Component | Role |
|---|---|
| **Razor Pages** | Chat (`/`) and Models (`/Models`) pages |
| **API Controller** | REST + SSE endpoints under `/api/` |
| **FoundryLocalService** | Adapter for Foundry Local REST API (download, delete, chat, catalog) |
| **ILlmProvider** | Provider interface for extensibility |

## Development (run locally)

```powershell
cd C:\Projects\FoundryLocalWebUI

# Ensure Foundry Local is running
foundry service start

# Run the app
dotnet run
```

Open `http://localhost:5207` in your browser.

## API Endpoints

| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/api/status` | Provider health check |
| `GET` | `/api/system-info` | System RAM info (for "Can Run" estimates) |
| `GET` | `/api/models` | List all models (downloaded + catalog) |
| `GET` | `/api/models/loaded` | List only currently loaded/ready models |
| `POST` | `/api/chat?provider=foundry` | Streaming chat completion (SSE) |
| `POST` | `/api/models/download` | Download a model with progress (SSE) |
| `DELETE` | `/api/models/{modelId}` | Remove a downloaded model from cache |
| `POST` | `/api/reconnect` | Re-discover Foundry Local endpoint |

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

Edit `appsettings.json`:

```json
{
  "LlmProviders": {
    "Foundry": {
      "Endpoint": ""
    }
  }
}
```

| Setting | Default | Notes |
|---|---|---|
| `Foundry:Endpoint` | *(blank — auto-detect)* | Set explicitly (e.g., `http://localhost:5273`) if auto-detection fails from IIS |

> **Tip**: Pin the Foundry Local port with `foundry service set --port 5273` for consistent auto-detection.

## Project Structure

```
FoundryLocalWebUI/
├── Controllers/
│   └── ApiController.cs          # REST + SSE API endpoints
├── Models/
│   └── LlmModels.cs              # DTOs (ChatMessage, ModelInfo, etc.)
├── Services/
│   ├── ILlmProvider.cs           # Provider interface
│   ├── FoundryLocalService.cs    # Foundry Local adapter (REST API only)
│   └── InMemoryLogStore.cs       # Ring buffer for application log capture
├── Pages/
│   ├── Index.cshtml              # Chat page with status panel
│   ├── Models.cshtml             # Model management (download/remove)
│   ├── Logs.cshtml               # Log viewer (app, IIS, Foundry, Event Log)
│   └── Shared/_Layout.cshtml     # Sidebar layout (dark theme, status indicator)
├── wwwroot/
│   ├── css/site.css              # Custom styles
│   └── js/
│       ├── site.js               # Sidebar toggle, Foundry status check
│       ├── chat.js               # Chat UI logic + SSE streaming
│       ├── models.js             # Model listing, download, remove, sorting
│       └── logs.js               # Log viewer UI with tabs and filtering
├── Program.cs                    # App startup and DI configuration
├── appsettings.json              # Configuration (Foundry endpoint)
├── web.config                    # IIS hosting configuration
├── Install-FoundryWebUI.ps1      # Automated deployment script
└── DEPLOYMENT.md                 # Full deployment & troubleshooting guide
```

## Troubleshooting

| Symptom | Likely Cause | Fix |
|---|---|---|
| Foundry shows red indicator in nav bar | Foundry Local not running or unreachable | Start it: `foundry service start` |
| No models listed | Foundry Local not connected | Check endpoint in appsettings.json or click 🔄 Reconnect on home page |
| Download fails | Foundry Local REST API error | Ensure Foundry Local is running; check IIS stdout logs |
| Remove fails | File permissions or model still loaded | Ensure IIS app pool identity has write access to Foundry cache directory |
| HTTP 500.30 on IIS | Hosting Bundle not installed | See [DEPLOYMENT.md — Troubleshooting](DEPLOYMENT.md#troubleshooting) |
| Chat shows no response | JSON casing mismatch or model not loaded | Check IIS stdout logs; ensure a model is loaded |
| Can't access from other machines | Windows Firewall blocking port | `New-NetFirewallRule -DisplayName "FoundryLocalWebUI" -Direction Inbound -Protocol TCP -LocalPort 80 -Action Allow` |

For the complete troubleshooting guide, see [DEPLOYMENT.md](DEPLOYMENT.md#troubleshooting).

## Roadmap

- [ ] **Phase 2**: Conversation persistence (save/load chat history)
- [ ] **Phase 2**: System prompt customization
- [ ] **Phase 2**: Model parameter tuning (temperature, top_p, max_tokens)
- [ ] **Phase 3**: Multi-user support with session isolation
- [ ] **Phase 3**: File upload and document Q&A
- [ ] **Phase 3**: RAG (Retrieval-Augmented Generation) integration

## License

MIT
