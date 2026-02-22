# FoundryLocalWebUI — Deployment & Troubleshooting Guide

This guide walks through a complete deployment of FoundryLocalWebUI on a **fresh Windows Server 2025** installation (Desktop Experience) with no additional roles or features pre-installed.

> **Note**: FoundryLocalWebUI supports **Foundry Local only**. Ollama is not supported.

> **All commands must be run in an elevated (Run as Administrator) PowerShell session** unless otherwise noted.

## Table of Contents

- [Automated Installation Script](#automated-installation-script)
- [Prerequisites](#prerequisites)
- [Step 1: Verify and Configure WinGet](#step-1-verify-and-configure-winget)
- [Step 2: Install IIS with Required Features](#step-2-install-iis-with-required-features)
- [Step 3: Install the .NET 8.0 Hosting Bundle and SDK](#step-3-install-the-net-80-hosting-bundle-and-sdk)
- [Step 4: Install LLM Providers](#step-4-install-llm-providers)
- [Step 5: Build and Publish the Application](#step-5-build-and-publish-the-application)
- [Step 6: Create the IIS Website](#step-6-create-the-iis-website)
- [Step 7: Configure Application Settings](#step-7-configure-application-settings)
- [Step 8: Configure App Pool Identity Permissions](#step-8-configure-app-pool-identity-permissions)
- [Step 9: Configure Windows Firewall](#step-9-configure-windows-firewall)
- [Step 10: Verify the Deployment](#step-10-verify-the-deployment)
- [Updating the Application](#updating-the-application)
  - [Recommended: Use the installer script](#recommended-use-the-installer-script)
  - [Manual update](#manual-update)
- [Troubleshooting](#troubleshooting)

---

## Automated Installation Script

A PowerShell script (`Install-FoundryLocalWebUI.ps1`) automates the entire installation and update process.

**First run** — installs all prerequisites (IIS, .NET, Foundry Local) and deploys the app.
**Subsequent runs** — detects the existing installation, skips prerequisites, rebuilds from source, and redeploys while **preserving your `appsettings.json` customizations**.

```powershell
# First-time install (elevated PowerShell)
.\Install-FoundryLocalWebUI.ps1

# Update after a git pull (elevated PowerShell)
git reset --hard origin/main   # if local changes exist
git pull
.\Install-FoundryLocalWebUI.ps1

# Options:
.\Install-FoundryLocalWebUI.ps1 -Port 8080                # Use port 8080 instead of 80
.\Install-FoundryLocalWebUI.ps1 -SkipPrerequisites        # Skip all prereq checks (fast redeploy)
.\Install-FoundryLocalWebUI.ps1 -SourcePath "C:\Build"    # Deploy from a pre-built publish folder
```

See [Appendix: Automated Setup Script](#appendix-automated-setup-script) for full parameter reference.

---

## Prerequisites

| Requirement | Minimum | Notes |
|---|---|---|
| **Operating System** | Windows Server 2025 Desktop Experience | Also works on Windows Server 2022, Windows 10/11 |
| **RAM** | 16 GB (32 GB+ recommended) | LLM models are memory-intensive |
| **Disk Space** | 50 GB free | ~50 MB for the app; LLM models require 2–30 GB each |
| **CPU** | 4+ cores | GPU (NVIDIA/AMD) recommended for faster inference |
| **Network** | Internet access during setup | Required to download .NET, WinGet packages, and LLM models |
| **Privileges** | Local Administrator | All installation steps require elevation |

---

## Step 1: Verify and Configure WinGet

Windows Server 2025 Desktop Experience includes WinGet (Windows Package Manager) via the App Installer package, but it may require registration on first use.

### Check if WinGet is available

```powershell
winget --version
```

If you see a version number (e.g., `v1.9.x`), skip to Step 2.

### If WinGet is not recognized

```powershell
# Re-register the App Installer package (fixes most "not recognized" errors)
Add-AppxPackage -RegisterByFamilyName -MainPackage Microsoft.DesktopAppInstaller_8wekyb3d8bbwe
```

Close and reopen your PowerShell session, then verify:

```powershell
winget --version
```

### If App Installer is missing entirely

This can happen on Server Core or custom images. Download and install manually:

```powershell
# Download the latest WinGet release from GitHub
$wingetUrl = "https://github.com/microsoft/winget-cli/releases/latest/download/Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle"
$wingetPath = "$env:TEMP\WinGet.msixbundle"
Invoke-WebRequest -Uri $wingetUrl -OutFile $wingetPath

# Install WinGet (requires the VCLibs dependency)
$vclibsUrl = "https://aka.ms/Microsoft.VCLibs.x64.14.00.Desktop.appx"
$vclibsPath = "$env:TEMP\VCLibs.appx"
Invoke-WebRequest -Uri $vclibsUrl -OutFile $vclibsPath
Add-AppxPackage -Path $vclibsPath
Add-AppxPackage -Path $wingetPath

# Clean up
Remove-Item $wingetPath, $vclibsPath -Force
```

> ⚠️ **Server Core**: WinGet support on Server Core is limited. If WinGet cannot be installed, use the direct download methods noted in each step below.

---

## Step 2: Install IIS with Required Features

A default Windows Server 2025 installation does not include IIS. Install the Web Server role with all sub-features needed by FoundryLocalWebUI:

```powershell
Install-WindowsFeature -Name `
    Web-Server, `
    Web-Common-Http, `
    Web-Default-Doc, `
    Web-Dir-Browsing, `
    Web-Http-Errors, `
    Web-Static-Content, `
    Web-Http-Logging, `
    Web-Request-Monitor, `
    Web-Filtering, `
    Web-Stat-Compression, `
    Web-Dyn-Compression, `
    Web-WebSockets, `
    Web-AppInit, `
    Web-ISAPI-Ext, `
    Web-ISAPI-Filter, `
    Web-Asp-Net45, `
    Web-Mgmt-Tools, `
    Web-Mgmt-Console `
    -IncludeManagementTools
```

### What each feature provides

| Feature | Why it's needed |
|---|---|
| `Web-Server` | Base IIS role |
| `Web-Common-Http` | Standard HTTP features (default docs, errors, static content) |
| `Web-Static-Content` | Serves CSS, JS, and other static files |
| `Web-Http-Logging` | Request logging for diagnostics |
| `Web-Request-Monitor` | Runtime monitoring and status |
| `Web-Filtering` | Request filtering for security |
| `Web-Stat-Compression` | Gzip compression for static files |
| `Web-Dyn-Compression` | Gzip compression for dynamic responses |
| `Web-WebSockets` | **Required for Server-Sent Events (SSE) streaming chat** |
| `Web-AppInit` | Application warm-up (prevents cold-start delays) |
| `Web-ISAPI-Ext` / `Web-ISAPI-Filter` | Required by the ASP.NET Core Module |
| `Web-Asp-Net45` | Required dependency for the ASP.NET Core Module installer |
| `Web-Mgmt-Tools` / `Web-Mgmt-Console` | IIS Manager GUI |

### Verify IIS is running

```powershell
Get-Service W3SVC | Select-Object Status, Name, DisplayName
```

Expected output: `Status: Running`.

You can also open a browser and navigate to `http://localhost` — you should see the default IIS welcome page.

---

## Step 3: Install the .NET 8.0 Hosting Bundle and SDK

### 3a: Install the .NET 8.0 Hosting Bundle

The Hosting Bundle includes the .NET Runtime, ASP.NET Core Runtime, and the ASP.NET Core IIS Module (ANCM v2). **IIS must be installed first** (Step 2).

**Option 1 — Install via WinGet:**

```powershell
winget install --id Microsoft.DotNet.HostingBundle.8 --silent --accept-package-agreements --accept-source-agreements
```

**Option 2 — Download and install manually:**

```powershell
# Download the .NET 8.0 Hosting Bundle
$hostingBundleUrl = "https://download.visualstudio.microsoft.com/download/pr/hosting-bundle-8.0/dotnet-hosting-win.exe"
$installerPath = "$env:TEMP\dotnet-hosting-8.0-win.exe"

# Get the actual download URL from the .NET download page
# Visit https://dotnet.microsoft.com/download/dotnet/8.0 and copy the Hosting Bundle link
# Example (version will vary):
Invoke-WebRequest -Uri "https://dotnet.microsoft.com/download/dotnet/thank-you/runtime-aspnetcore-8.0.12-windows-hosting-bundle-installer" -OutFile $installerPath

# Silent install
Start-Process -FilePath $installerPath -ArgumentList "/install", "/quiet", "/norestart" -Wait -NoNewWindow

# Clean up
Remove-Item $installerPath -Force
```

> 💡 **Tip**: For the most current direct download URL, visit https://dotnet.microsoft.com/download/dotnet/8.0 and look for **Hosting Bundle** under the ASP.NET Core Runtime section.

### Restart IIS after Hosting Bundle installation

```powershell
net stop was /y
net start w3svc
```

### Verify the Hosting Bundle is installed

```powershell
# Check .NET runtimes
dotnet --list-runtimes
```

You should see both:
- `Microsoft.NETCore.App 8.0.x`
- `Microsoft.AspNetCore.App 8.0.x`

```powershell
# Verify the ASP.NET Core Module is registered in IIS
Import-Module WebAdministration
Get-WebGlobalModule | Where-Object { $_.Name -like "*AspNetCore*" }
```

Expected output: `AspNetCoreModuleV2`.

### 3b: Install the .NET 8.0 SDK (required for building from source)

If you are building the application on this server (rather than deploying a pre-built publish folder), you also need the SDK:

```powershell
winget install --id Microsoft.DotNet.SDK.8 --silent --accept-package-agreements --accept-source-agreements
```

Verify:

```powershell
dotnet --version
```

Expected output: `8.0.x` (e.g., `8.0.404`).

> ℹ️ The SDK is **not required** if you publish the app on a separate build machine and copy the output to the server. Only the Hosting Bundle is needed on the server in that case.

---

## Step 4: Install LLM Providers

### 4a: Foundry Local (required)

```powershell
winget install --id Microsoft.FoundryLocal --silent --accept-package-agreements --accept-source-agreements
```

After installation, close and reopen your PowerShell session (so `foundry` is on your PATH), then:

```powershell
# Start the Foundry Local service
foundry service start

# Check the status and note the endpoint URL
foundry service status
```

The output will display the Foundry Local endpoint URL (e.g., `http://localhost:5273`). **Record this URL** — you will need it if auto-detection does not work from IIS.

#### Pin Foundry to a consistent port (recommended)

By default, Foundry Local uses a random port on each start. Pin it to port 5273:

```powershell
foundry service set --port 5273
foundry service start
```

#### Download a model for testing

```powershell
# List available models in the catalog
foundry model list

# Download and run a small model for testing
foundry model run phi-3.5-mini
```

---

## Step 5: Build and Publish the Application

### If building on the server

```powershell
# Navigate to the project directory
cd C:\Projects\FoundryLocalWebUI

# Restore dependencies and publish
dotnet publish -c Release -o C:\inetpub\FoundryLocalWebUI
```

### If building on a separate machine

On your build machine:
```powershell
cd C:\Projects\FoundryLocalWebUI
dotnet publish -c Release -o C:\Publish\FoundryLocalWebUI
```

Then copy the publish folder to the server:
```powershell
# From the build machine (adjust server name)
Copy-Item -Path "C:\Publish\FoundryLocalWebUI\*" -Destination "\\SERVER\C$\inetpub\FoundryLocalWebUI" -Recurse -Force
```

Or use any file transfer method (RDP file copy, SCP, USB, etc.).

### Verify published output

```powershell
# Both files must exist
Test-Path C:\inetpub\FoundryLocalWebUI\FoundryWebUI.dll
Test-Path C:\inetpub\FoundryLocalWebUI\web.config
```

Both should return `True`.

---

## Step 6: Create the IIS Website

### Stop the Default Web Site (if using port 80)

```powershell
$appcmd = "$env:SystemRoot\System32\inetsrv\appcmd.exe"
& $appcmd stop site "Default Web Site"
```

### Option A: Using IIS Manager (GUI)

1. Open **IIS Manager** (`inetmgr`).
2. Right-click **Sites** → **Add Website**.
3. Configure:
   - **Site name**: `FoundryLocalWebUI`
   - **Application pool**: Click **Select** → create a new pool named `FoundryLocalWebUI` with **No Managed Code** as the .NET CLR version.
   - **Physical path**: `C:\inetpub\FoundryLocalWebUI`
   - **Binding**: `http`, port `80` (or your preferred port), hostname as needed.
4. Click **OK**.

### Option B: Using appcmd.exe (recommended for automation)

```powershell
$appcmd = "$env:SystemRoot\System32\inetsrv\appcmd.exe"

# Create the Application Pool
& $appcmd add apppool /name:"FoundryLocalWebUI"
& $appcmd set apppool "FoundryLocalWebUI" /managedRuntimeVersion:""
& $appcmd set apppool "FoundryLocalWebUI" /managedPipelineMode:"Integrated"

# Disable idle timeout (prevents app pool shutdown during inactivity)
& $appcmd set apppool "FoundryLocalWebUI" /processModel.idleTimeout:"00:00:00"

# Enable AlwaysRunning start mode (reduces cold-start delays)
& $appcmd set apppool "FoundryLocalWebUI" /startMode:"AlwaysRunning"

# Create the Website
& $appcmd add site /name:"FoundryLocalWebUI" /physicalPath:"C:\inetpub\FoundryLocalWebUI" /bindings:"http/*:80:"
& $appcmd set site "FoundryLocalWebUI" /[path='/'].applicationPool:"FoundryLocalWebUI"

# If port 80 is already in use, replace 80 above with your chosen port (e.g., 8080)
```

> ⚠️ **Note**: The `WebAdministration` PowerShell module (`Import-Module WebAdministration`) and its `IIS:\` drive may not work reliably on Windows Server 2025. Use `appcmd.exe` or the IIS Manager GUI instead.

### Application Pool Settings Reference

| Setting | Value | Why |
|---|---|---|
| .NET CLR Version | **No Managed Code** | ASP.NET Core runs out-of-band, not via the classic .NET CLR |
| Managed Pipeline Mode | **Integrated** | Required for the ASP.NET Core Module |
| Start Mode | **AlwaysRunning** | Prevents cold-start delays when the first user connects |
| Idle Timeout | **0 (disabled)** | Prevents the app pool from shutting down during periods of inactivity |

---

## Step 7: Configure Application Settings

Edit `C:\inetpub\FoundryLocalWebUI\appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "LlmProviders": {
    "Foundry": {
      "Endpoint": "http://localhost:5273"
    }
  }
}
```

### Configuration Notes

| Setting | Description |
|---|---|
| `LlmProviders:Foundry:Endpoint` | Leave **blank** to auto-detect via port scanning. Set explicitly (e.g., `http://localhost:5273`) for reliability. |
| `AllowedHosts` | Set to `*` for open access. Restrict to specific hostnames for security (e.g., `myserver.contoso.com`). |

### Environment-Specific Overrides

You can also configure settings via environment variables on the Application Pool:

```powershell
# Example: Set Foundry endpoint via environment variable
# In IIS Manager → Application Pool → Advanced Settings → Environment Variables
# Or via Configuration Editor for the site
```

Or create an `appsettings.Production.json` alongside `appsettings.json`:

```json
{
  "LlmProviders": {
    "Foundry": {
      "Endpoint": "http://localhost:5273"
    }
  }
}
```

---

## Step 8: Configure App Pool Identity Permissions

The IIS Application Pool identity needs:

1. **Read/Execute access** to the application folder:
   ```powershell
   $acl = Get-Acl "C:\inetpub\FoundryLocalWebUI"
   $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
       "IIS AppPool\FoundryLocalWebUI", "ReadAndExecute", "ContainerInherit,ObjectInherit", "None", "Allow")
   $acl.SetAccessRule($rule)
   Set-Acl "C:\inetpub\FoundryLocalWebUI" $acl
   ```

2. **Write access** to the logs folder (if stdout logging is enabled):
   ```powershell
   New-Item -ItemType Directory -Path "C:\inetpub\FoundryLocalWebUI\logs" -Force
   $acl = Get-Acl "C:\inetpub\FoundryLocalWebUI\logs"
   $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
       "IIS AppPool\FoundryLocalWebUI", "Modify", "ContainerInherit,ObjectInherit", "None", "Allow")
   $acl.SetAccessRule($rule)
   Set-Acl "C:\inetpub\FoundryLocalWebUI\logs" $acl
   ```

3. **Permission to execute `foundry` CLI** — The App Pool identity must be able to run `foundry service status` for endpoint auto-detection. If this fails, set the endpoint explicitly in `appsettings.json`.

---

## Step 9: Configure Windows Firewall

By default, Windows Server 2025 blocks inbound traffic. If you want other machines on the network to access FoundryLocalWebUI, open the port in Windows Firewall:

```powershell
# Allow inbound HTTP on port 80 (adjust port if you chose a different one)
New-NetFirewallRule `
    -DisplayName "FoundryLocalWebUI - HTTP Inbound" `
    -Direction Inbound `
    -Protocol TCP `
    -LocalPort 80 `
    -Action Allow `
    -Profile Domain,Private

# Verify the rule was created
Get-NetFirewallRule -DisplayName "FoundryLocalWebUI*" | Format-Table Name, DisplayName, Enabled, Action
```

> ℹ️ If you only need local access (same machine), you can skip this step — `localhost` traffic bypasses the firewall.

> ⚠️ If using a port other than 80, replace `80` in the command above with your chosen port number.

---

## Step 10: Verify the Deployment

1. **Browse to the site**:
   Open a browser and navigate to `http://localhost` (or the port you configured).

2. **Check the status indicator** in the top-right corner of the nav bar:
   - **Bright green square** + "Foundry Local Connection" = Connected
   - **Bright red square** + "Foundry Local Connection" = Disconnected

3. **Test the API directly**:
   ```powershell
   # Check provider status
   Invoke-RestMethod http://localhost/api/status

   # List available models
   Invoke-RestMethod http://localhost/api/models
   ```

4. **Test chat** (select a loaded model on the Chat page and send a message).

---

## Updating the Application

### Recommended: Use the installer script

The easiest way to update is to `git pull` and re-run the installer. It automatically detects the existing deployment and performs an in-place update:

```powershell
# On the server, from your FoundryLocalWebUI source directory
cd C:\path\to\FoundryLocalWebUI
git pull
.\Install-FoundryLocalWebUI.ps1
```

**What the script does on update:**
1. Detects that `C:\inetpub\FoundryLocalWebUI\FoundryWebUI.dll` already exists → enters **update mode**
2. Skips all prerequisite installation (IIS, .NET, Foundry Local, Ollama)
3. Stops the IIS site and app pool
4. Backs up your current `appsettings.json` (preserving endpoint overrides and other customizations)
5. Rebuilds and republishes from source (`dotnet publish`)
6. Restores your backed-up `appsettings.json`
7. Restarts the IIS site

> 💡 **Tip**: Use `-SkipPrerequisites` to skip even the prerequisite detection step for the fastest possible redeploy:
> ```powershell
> .\Install-FoundryLocalWebUI.ps1 -SkipPrerequisites
> ```

### Manual update

If you prefer to update manually:

1. **Stop the site** (to unlock DLL files):
   ```powershell
   $appcmd = "$env:SystemRoot\System32\inetsrv\appcmd.exe"
   & $appcmd stop site "FoundryLocalWebUI"
   & $appcmd stop apppool "FoundryLocalWebUI"
   ```

2. **Back up your settings**:
   ```powershell
   Copy-Item C:\inetpub\FoundryLocalWebUI\appsettings.json C:\inetpub\FoundryLocalWebUI\appsettings.json.bak
   ```

3. **Republish**:
   ```powershell
   cd C:\path\to\FoundryLocalWebUI
   dotnet publish -c Release -o C:\inetpub\FoundryLocalWebUI
   ```

4. **Restore your settings**:
   ```powershell
   Copy-Item C:\inetpub\FoundryLocalWebUI\appsettings.json.bak C:\inetpub\FoundryLocalWebUI\appsettings.json
   ```

5. **Restart**:
   ```powershell
   & $appcmd start apppool "FoundryLocalWebUI"
   & $appcmd start site "FoundryLocalWebUI"
   ```

> 💡 **Tip**: For zero-downtime updates, place a file named `app_offline.htm` in the publish folder before copying new files, then delete it after the update.

---

## Troubleshooting

### 1. HTTP 500.30 — ASP.NET Core app failed to start

**Symptoms**: Browser shows "HTTP Error 500.30 - ASP.NET Core app failed to start".

**Steps**:
1. Enable stdout logging in `web.config`:
   ```xml
   <aspNetCore processPath="dotnet" arguments=".\FoundryWebUI.dll"
               stdoutLogEnabled="true" stdoutLogFile=".\logs\stdout"
               hostingModel="inprocess" />
   ```
2. Create the `logs` folder with write permissions (see Step 7).
3. Restart the site and check `C:\inetpub\FoundryLocalWebUI\logs\` for log files.
4. Common causes:
   - **.NET 8.0 Hosting Bundle not installed** — Verify with `dotnet --list-runtimes`.
   - **Missing DLLs** — Re-run `dotnet publish`.
   - **appsettings.json syntax error** — Validate JSON formatting.

### 2. HTTP 502.5 — Process failure

**Symptoms**: "HTTP Error 502.5 - ANCM Out-Of-Process Startup Failure".

**Steps**:
1. This typically means the `dotnet` executable cannot be found.
2. Verify `dotnet` is on the system PATH:
   ```powershell
   where.exe dotnet
   ```
3. If not found, add the .NET install directory to the system PATH, or use the full path in `web.config`:
   ```xml
   <aspNetCore processPath="C:\Program Files\dotnet\dotnet.exe" ... />
   ```

### 3. Models page shows "No models found"

**Steps**:
1. Check that Foundry Local is running:
   ```powershell
   foundry service status
   ```
2. If Foundry Local is running but the app can't detect it, set the endpoint explicitly in `appsettings.json`:
   ```json
   "Foundry": {
     "Endpoint": "http://localhost:5273"
   }
   ```
3. Verify from the server itself:
   ```powershell
   Invoke-RestMethod http://localhost/api/status
   ```

### 4. Chat returns errors or no response

**Steps**:
1. Ensure at least one model is **loaded** (not just downloaded).
2. For Foundry Local, models must be explicitly loaded:
   ```powershell
   foundry model run phi-3.5-mini
   ```
3. Check the app logs for HTTP timeout errors — large models may take time to load on first request.
4. If chat shows empty bubbles, check the IIS stdout logs for JSON parsing errors. The app uses camelCase JSON serialization.

### 5. Foundry Local auto-detection fails

**Symptoms**: Foundry shows as unavailable (red ✗) even though `foundry service status` works in a terminal.

**Cause**: The app probes ports 5272-5274 to discover Foundry Local. If Foundry uses a random port, auto-detection fails.

**Solutions** (choose one):
- **Recommended**: Pin the Foundry port and set the endpoint explicitly in `appsettings.json`:
  ```powershell
  foundry service set --port 5273
  ```
  Then set `"Endpoint": "http://localhost:5273"` in `appsettings.json`.
- Change Foundry to use a port in the 5272-5274 range (auto-detected).

### 6. Streaming responses don't work (messages appear all at once)

**Steps**:
1. Check if a **reverse proxy** (e.g., ARR, nginx) is buffering SSE responses.
2. For IIS with ARR, disable response buffering:
   ```xml
   <!-- In web.config, inside <system.webServer> -->
   <httpProtocol>
     <customHeaders>
       <add name="X-Accel-Buffering" value="no" />
     </customHeaders>
   </httpProtocol>
   ```
3. If using **Cloudflare** or another CDN, ensure SSE/streaming is not being buffered.

### 7. App works locally but not from other machines

**Steps**:
1. Check **Windows Firewall** — allow inbound TCP on the configured port:
   ```powershell
   New-NetFirewallRule -DisplayName "FoundryLocalWebUI" -Direction Inbound -Protocol TCP -LocalPort 80 -Action Allow
   ```
2. Check IIS **bindings** — ensure the site is bound to `*` (all IPs), not just `localhost`:
   - IIS Manager → Sites → FoundryLocalWebUI → Bindings → Ensure IP address is set to **All Unassigned**.

### 8. High memory usage

LLM models are memory-intensive. The Models page shows estimated RAM and a "Can Run" indicator for each model. Monitor with:
```powershell
Get-Process -Name "dotnet", "foundry*" | Select-Object Name, WorkingSet64, CPU
```

- **Foundry Local**: Each loaded model consumes RAM proportional to its size (~1.2× file size).
- The FoundryLocalWebUI app itself uses minimal memory (~50–100 MB).
- Use `foundry model unload <model>` to free memory from loaded models.

### 9. Model download or remove fails

**Possible causes**:
- **Download fails**: Foundry Local service not reachable, or the download API returned an error. Check the app logs.
- **Remove fails**: The IIS app pool identity doesn't have write access to the Foundry cache directory.

**Solution for remove failures**:
1. Find the Foundry cache location:
   ```powershell
   foundry cache location
   ```
2. Grant the IIS app pool identity write access:
   ```powershell
   $cachePath = "C:\Users\Administrator\.foundry\cache\models"
   icacls $cachePath /grant "IIS AppPool\FoundryLocalWebUI:(OI)(CI)F" /T
   ```
3. Restart the IIS site.

### 10. Foundry CLI commands reference

| Task | CLI Command |
|---|---|
| Start service | `foundry service start` |
| Pin port | `foundry service set --port 5273` |
| List catalog | `foundry model list` |
| Download model | `foundry model download <model-alias>` |
| Remove from cache | `foundry cache remove <model-id>` |
| Load into memory | `foundry model load <model>` |
| Unload from memory | `foundry model unload <model>` |
| Check cache location | `foundry cache location` |

### 11. Event Log Inspection

When all else fails, check the Windows Event Log:
```powershell
Get-WinEvent -LogName "Application" -MaxEvents 20 |
    Where-Object { $_.ProviderName -match "IIS|ASP.NET|.NET" } |
    Format-Table TimeCreated, Message -Wrap
```

---

## Architecture Summary

```
┌──────────────────────────────────────────────────────────────┐
│                    Browser (Client)                          │
│   Chat Page (/)          Models Page (/Models)               │
└──────────────────┬───────────────────────────────────────────┘
                   │ HTTP / SSE
┌──────────────────▼───────────────────────────────────────────┐
│              IIS + ASP.NET Core Module v2                     │
│              FoundryLocalWebUI (in-process)                        │
│                                                              │
│   /api/status        → check Foundry Local health            │
│   /api/system-info   → system RAM for "Can Run" estimates    │
│   /api/models        → list models (downloaded + catalog)    │
│   /api/chat          → streaming chat (SSE)                  │
│   /api/models/download → download model (via CLI)            │
│   /api/models/{id}   → remove model (via CLI cache remove)   │
│   /api/reconnect     → re-discover Foundry endpoint          │
└────────┬─────────────────────────────────────────────────────┘
         │
         ▼
┌─────────────────────┐
│   Foundry Local     │
│  http://localhost:   │
│       5273          │
│                     │
│ /openai/models      │
│ /foundry/list       │
│ /v1/chat/completions│
│ /openai/load/{name} │
└─────────────────────┘
```

---

## Appendix: Automated Setup Script

The `Install-FoundryLocalWebUI.ps1` script automates the entire installation and update process. It is included in the project root.

### Parameters

| Parameter | Default | Description |
|---|---|---|
| `-Port` | `80` | IIS website port |
| `-SiteName` | `FoundryLocalWebUI` | IIS site name |
| `-AppPoolName` | `FoundryLocalWebUI` | IIS application pool name |
| `-InstallPath` | `C:\inetpub\FoundryLocalWebUI` | Published application directory |
| `-SourcePath` | *(empty)* | Path to a pre-built publish folder. If omitted, builds from the project in the script's directory |
| `-SkipFirewall` | `$false` | Skip firewall rule creation |
| `-FoundryEndpoint` | *(empty)* | Explicit Foundry Local endpoint URL (e.g., `http://localhost:5273`). Leave empty for auto-detection |
| `-FoundryPort` | `5273` | Port to pin Foundry Local to via `foundry service set --port` |
| `-SkipPrerequisites` | `$false` | Skip all prerequisite checks. Use for fast redeployment when prerequisites are already installed |

### Behavior

- **Fresh install** (no existing deployment at `InstallPath`): Installs IIS, .NET, Foundry Local, creates IIS site, deploys the app, and configures the Foundry endpoint.
- **Update** (existing `FoundryWebUI.dll` found at `InstallPath`): Stops IIS site, backs up `appsettings.json`, rebuilds, restores settings, restarts site. Prerequisites are automatically skipped.

### Usage Examples

```powershell
# Full installation on a fresh server
.\Install-FoundryLocalWebUI.ps1

# Update after git pull
git reset --hard origin/main
git pull
.\Install-FoundryLocalWebUI.ps1

# Fast redeploy (skip prereq checks)
.\Install-FoundryLocalWebUI.ps1 -SkipPrerequisites

# Install on a custom port
.\Install-FoundryLocalWebUI.ps1 -Port 8080

# Deploy from a pre-built publish folder
.\Install-FoundryLocalWebUI.ps1 -SourcePath "C:\Build\FoundryLocalWebUI"

# Set explicit Foundry endpoint
.\Install-FoundryLocalWebUI.ps1 -FoundryEndpoint "http://localhost:5273"
```
