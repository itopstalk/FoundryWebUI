#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Automated setup and update script for FoundryWebUI on Windows Server 2025.

.DESCRIPTION
    Installs all prerequisites and deploys FoundryWebUI as an IIS website.
    Safe to re-run after a git pull — detects existing installations and only
    rebuilds/redeploys the app, preserving your appsettings.json customizations.

    On first run:  Installs IIS, .NET, Foundry Local, creates IIS site, etc.
    On subsequent runs:  Stops IIS site, rebuilds from source, redeploys, restarts.

    Prerequisites installed (first run only):
    - IIS with required features (WebSockets, compression, etc.)
    - .NET 8.0 Hosting Bundle
    - .NET 8.0 SDK (if building from source)
    - Microsoft Foundry Local
    - Ollama (optional)

.PARAMETER Port
    The port for the IIS website. Default: 80.

.PARAMETER SiteName
    The IIS site name. Default: FoundryWebUI.

.PARAMETER AppPoolName
    The IIS application pool name. Default: FoundryWebUI.

.PARAMETER InstallPath
    Where to publish the application. Default: C:\inetpub\FoundryWebUI.

.PARAMETER SourcePath
    Path to a pre-built publish folder. If omitted, the script builds from the project
    in the same directory as this script.

.PARAMETER SkipOllama
    Skip Ollama installation.

.PARAMETER SkipFirewall
    Skip firewall rule creation.

.PARAMETER FoundryEndpoint
    Explicit Foundry Local endpoint URL. Leave empty for auto-detection.

.PARAMETER SkipPrerequisites
    Skip all prerequisite checks (WinGet, IIS, .NET, LLM providers).
    Useful when you know prerequisites are already installed and just want to redeploy.

.EXAMPLE
    .\Install-FoundryWebUI.ps1
    # First run: full install. Subsequent runs: rebuild and redeploy only.

.EXAMPLE
    .\Install-FoundryWebUI.ps1 -SkipPrerequisites
    # Fast update: just rebuild from source and redeploy.

.EXAMPLE
    .\Install-FoundryWebUI.ps1 -Port 8080 -SkipOllama
#>

[CmdletBinding()]
param(
    [int]$Port = 80,
    [string]$SiteName = "FoundryWebUI",
    [string]$AppPoolName = "FoundryWebUI",
    [string]$InstallPath = "C:\inetpub\FoundryWebUI",
    [string]$SourcePath = "",
    [switch]$SkipOllama,
    [switch]$SkipFirewall,
    [string]$FoundryEndpoint = "",
    [int]$FoundryPort = 5273,
    [switch]$SkipPrerequisites
)

$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "  $Message" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host ""
}

function Write-Success {
    param([string]$Message)
    Write-Host "  ✅ $Message" -ForegroundColor Green
}

function Write-Warning2 {
    param([string]$Message)
    Write-Host "  ⚠️  $Message" -ForegroundColor Yellow
}

function Write-Info {
    param([string]$Message)
    Write-Host "  ℹ️  $Message" -ForegroundColor Gray
}

function Test-CommandExists {
    param([string]$Command)
    $null = Get-Command $Command -ErrorAction SilentlyContinue
    return $?
}

# ============================================================
# Pre-flight checks
# ============================================================
Write-Step "Pre-flight checks"

# Verify running as administrator
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "This script must be run as Administrator. Right-click PowerShell and select 'Run as Administrator'."
}
Write-Success "Running as Administrator"

# Check OS
$os = Get-CimInstance Win32_OperatingSystem
Write-Info "Operating System: $($os.Caption) ($($os.Version))"

# Detect if this is a fresh install or an update
$appcmd = "$env:SystemRoot\System32\inetsrv\appcmd.exe"
$isUpdate = (Test-Path $InstallPath) -and (Test-Path "$InstallPath\FoundryWebUI.dll")
$iisInstalled = Test-Path $appcmd

if ($isUpdate) {
    Write-Host ""
    Write-Host "  ========================================" -ForegroundColor Magenta
    Write-Host "  UPDATE MODE — Existing installation found" -ForegroundColor Magenta
    Write-Host "  ========================================" -ForegroundColor Magenta
    Write-Host "  Install path: $InstallPath" -ForegroundColor White
    Write-Host ""
    $SkipPrerequisites = $true
} else {
    Write-Info "Fresh installation detected"
}

# ============================================================
# Steps 1-4: Prerequisites (skipped on update)
# ============================================================
if ($SkipPrerequisites) {
    Write-Info "Skipping prerequisite installation (existing installation or -SkipPrerequisites)"
} else {

# ============================================================
# Step 1: Verify WinGet
# ============================================================
Write-Step "Step 1: Verifying WinGet (Windows Package Manager)"

if (Test-CommandExists "winget") {
    $wingetVer = (winget --version 2>$null)
    Write-Success "WinGet is available: $wingetVer"
} else {
    Write-Info "WinGet not found. Attempting to register App Installer..."
    try {
        Add-AppxPackage -RegisterByFamilyName -MainPackage Microsoft.DesktopAppInstaller_8wekyb3d8bbwe -ErrorAction Stop
        # Refresh PATH
        $env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path", "User")
        if (Test-CommandExists "winget") {
            Write-Success "WinGet registered successfully"
        } else {
            Write-Warning2 "WinGet registration attempted but command still not available."
            Write-Warning2 "Trying manual download from GitHub..."

            $vclibsUrl = "https://aka.ms/Microsoft.VCLibs.x64.14.00.Desktop.appx"
            $vclibsPath = "$env:TEMP\VCLibs.appx"
            Invoke-WebRequest -Uri $vclibsUrl -OutFile $vclibsPath -UseBasicParsing
            Add-AppxPackage -Path $vclibsPath

            $wingetUrl = "https://github.com/microsoft/winget-cli/releases/latest/download/Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle"
            $wingetPath = "$env:TEMP\WinGet.msixbundle"
            Invoke-WebRequest -Uri $wingetUrl -OutFile $wingetPath -UseBasicParsing
            Add-AppxPackage -Path $wingetPath

            Remove-Item $vclibsPath, $wingetPath -Force -ErrorAction SilentlyContinue

            $env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path", "User")
            if (Test-CommandExists "winget") {
                Write-Success "WinGet installed successfully from GitHub"
            } else {
                throw "Failed to install WinGet. Please install manually from https://github.com/microsoft/winget-cli/releases"
            }
        }
    } catch {
        throw "Failed to install WinGet: $_"
    }
}

# ============================================================
# Step 2: Install IIS with required features
# ============================================================
Write-Step "Step 2: Installing IIS with required features"

$iisFeatures = @(
    "Web-Server",
    "Web-Common-Http",
    "Web-Default-Doc",
    "Web-Dir-Browsing",
    "Web-Http-Errors",
    "Web-Static-Content",
    "Web-Http-Logging",
    "Web-Request-Monitor",
    "Web-Filtering",
    "Web-Stat-Compression",
    "Web-Dyn-Compression",
    "Web-WebSockets",
    "Web-AppInit",
    "Web-ISAPI-Ext",
    "Web-ISAPI-Filter",
    "Web-Asp-Net45",
    "Web-Mgmt-Tools",
    "Web-Mgmt-Console"
)

Write-Info "Installing features: $($iisFeatures -join ', ')"
$result = Install-WindowsFeature -Name $iisFeatures -IncludeManagementTools -ErrorAction Stop

if ($result.RestartNeeded -eq "Yes") {
    Write-Warning2 "A server restart is required. Please restart and re-run this script."
    Write-Warning2 "Run: Restart-Computer -Force"
    exit 1
}

# Verify IIS is running
$w3svc = Get-Service W3SVC -ErrorAction SilentlyContinue
if ($w3svc -and $w3svc.Status -eq "Running") {
    Write-Success "IIS installed and running"
} else {
    Start-Service W3SVC
    Write-Success "IIS installed and started"
}

# ============================================================
# Step 3: Install .NET 8.0 Hosting Bundle and SDK
# ============================================================
Write-Step "Step 3: Installing .NET 8.0 Hosting Bundle"

# Check if hosting bundle is already installed
$ancmInstalled = $false
try {
    Import-Module WebAdministration -ErrorAction SilentlyContinue
    $modules = Get-WebGlobalModule -ErrorAction SilentlyContinue | Where-Object { $_.Name -like "*AspNetCore*" }
    if ($modules) { $ancmInstalled = $true }
} catch { }

if ($ancmInstalled) {
    Write-Success ".NET Hosting Bundle already installed (ANCM v2 detected)"
} else {
    Write-Info "Installing .NET 8.0 Hosting Bundle via WinGet..."
    winget install --id Microsoft.DotNet.HostingBundle.8 --silent --accept-package-agreements --accept-source-agreements 2>&1 | Out-Null

    # Restart IIS to register the module
    Write-Info "Restarting IIS to register ASP.NET Core Module..."
    net stop was /y 2>&1 | Out-Null
    net start w3svc 2>&1 | Out-Null

    # Verify
    Import-Module WebAdministration -Force -ErrorAction SilentlyContinue
    $modules = Get-WebGlobalModule -ErrorAction SilentlyContinue | Where-Object { $_.Name -like "*AspNetCore*" }
    if ($modules) {
        Write-Success ".NET 8.0 Hosting Bundle installed and ANCM v2 registered"
    } else {
        Write-Warning2 "ANCM v2 not detected after install. You may need to restart the server and re-run."
    }
}

# Install SDK if building from source
if (-not $SourcePath) {
    Write-Step "Step 3b: Installing .NET 8.0 SDK (needed for building from source)"

    if (Test-CommandExists "dotnet") {
        $sdkList = dotnet --list-sdks 2>$null
        if ($sdkList -match "8\.0") {
            Write-Success ".NET 8.0 SDK already installed"
        } else {
            Write-Info "Installing .NET 8.0 SDK via WinGet..."
            winget install --id Microsoft.DotNet.SDK.8 --silent --accept-package-agreements --accept-source-agreements 2>&1 | Out-Null
            # Refresh PATH
            $env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path", "User")
            Write-Success ".NET 8.0 SDK installed"
        }
    } else {
        Write-Info "Installing .NET 8.0 SDK via WinGet..."
        winget install --id Microsoft.DotNet.SDK.8 --silent --accept-package-agreements --accept-source-agreements 2>&1 | Out-Null
        $env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path", "User")
        Write-Success ".NET 8.0 SDK installed"
    }
}

# ============================================================
# Step 4: Install LLM Providers
# ============================================================
Write-Step "Step 4a: Installing Microsoft Foundry Local"

if (Test-CommandExists "foundry") {
    Write-Success "Foundry Local already installed"
} else {
    Write-Info "Installing Foundry Local via WinGet..."
    winget install --id Microsoft.FoundryLocal --silent --accept-package-agreements --accept-source-agreements 2>&1 | Out-Null
    $env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path", "User")
    if (Test-CommandExists "foundry") {
        Write-Success "Foundry Local installed"
    } else {
        Write-Warning2 "Foundry Local installed but 'foundry' not found on PATH. You may need to open a new terminal."
    }
}

# Start Foundry service with a fixed port
if (Test-CommandExists "foundry") {
    Write-Info "Configuring Foundry Local to use fixed port $FoundryPort..."
    try {
        $setProc = Start-Process -FilePath "foundry" -ArgumentList "service", "set", "--port", "$FoundryPort" `
            -NoNewWindow -PassThru -RedirectStandardOutput "$env:TEMP\foundry-set.log" `
            -RedirectStandardError "$env:TEMP\foundry-set-err.log"
        $exited = $setProc.WaitForExit(15000)
        if (-not $exited) { try { $setProc.Kill() } catch { } }
        Write-Success "Foundry Local port set to $FoundryPort"
    } catch {
        Write-Warning2 "Could not set Foundry port: $_"
    }

    Write-Info "Starting Foundry Local service..."
    try {
        # Use Start-Process with a timeout — foundry CLI commands can block indefinitely
        $startProc = Start-Process -FilePath "foundry" -ArgumentList "service", "start" `
            -NoNewWindow -PassThru -RedirectStandardOutput "$env:TEMP\foundry-start.log" `
            -RedirectStandardError "$env:TEMP\foundry-start-err.log"
        $exited = $startProc.WaitForExit(30000) # 30 second timeout
        if (-not $exited) {
            Write-Warning2 "'foundry service start' did not complete within 30s (service may already be running)."
            try { $startProc.Kill() } catch { }
        }

        # Check status with a timeout
        $statusProc = Start-Process -FilePath "foundry" -ArgumentList "service", "status" `
            -NoNewWindow -PassThru -RedirectStandardOutput "$env:TEMP\foundry-status.log" `
            -RedirectStandardError "$env:TEMP\foundry-status-err.log"
        $exited = $statusProc.WaitForExit(15000) # 15 second timeout
        if ($exited -and (Test-Path "$env:TEMP\foundry-status.log")) {
            $status = Get-Content "$env:TEMP\foundry-status.log" -Raw
            Write-Success "Foundry Local service running"
            Write-Info "Status: $($status.Trim())"
        } else {
            if (-not $exited) { try { $statusProc.Kill() } catch { } }
            Write-Warning2 "'foundry service status' timed out. The service may still be starting."
            Write-Info "You can check manually later with: foundry service status"
        }

        # Clean up temp files
        Remove-Item "$env:TEMP\foundry-set.log", "$env:TEMP\foundry-set-err.log",
            "$env:TEMP\foundry-start.log", "$env:TEMP\foundry-start-err.log",
            "$env:TEMP\foundry-status.log", "$env:TEMP\foundry-status-err.log" -Force -ErrorAction SilentlyContinue
    } catch {
        Write-Warning2 "Could not start Foundry Local service: $_"
    }

    # Set the endpoint for appsettings.json if not explicitly provided
    if (-not $FoundryEndpoint) {
        $FoundryEndpoint = "http://localhost:$FoundryPort"
        Write-Info "Foundry endpoint will be set to $FoundryEndpoint in appsettings.json"
    }
}

if (-not $SkipOllama) {
    Write-Step "Step 4b: Installing Ollama"

    if (Test-CommandExists "ollama") {
        Write-Success "Ollama already installed"
    } else {
        Write-Info "Installing Ollama via WinGet..."
        winget install --id Ollama.Ollama --silent --accept-package-agreements --accept-source-agreements 2>&1 | Out-Null
        $env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path", "User")
        if (Test-CommandExists "ollama") {
            Write-Success "Ollama installed"
        } else {
            Write-Warning2 "Ollama installed but 'ollama' not found on PATH. You may need to open a new terminal."
        }
    }
} else {
    Write-Info "Skipping Ollama installation (--SkipOllama specified)"
}

} # End of prerequisites block

# ============================================================
# Step 5: Stop existing site, build, and publish
# ============================================================
if ($isUpdate) {
    Write-Step "Stopping IIS site for update"
    $appcmd = "$env:SystemRoot\System32\inetsrv\appcmd.exe"
    & $appcmd stop site $SiteName 2>$null
    & $appcmd stop apppool $AppPoolName 2>$null
    Start-Sleep -Seconds 2
    Write-Success "Site stopped"
}

# Preserve existing appsettings.json before publish overwrites it
$appSettingsBackup = $null
$appSettingsPath = Join-Path $InstallPath "appsettings.json"
if ($isUpdate -and (Test-Path $appSettingsPath)) {
    Write-Info "Backing up existing appsettings.json"
    $appSettingsBackup = Get-Content $appSettingsPath -Raw
}

Write-Step "Step 5: Publishing FoundryWebUI"

if ($SourcePath) {
    Write-Info "Copying pre-built application from $SourcePath"
    if (-not (Test-Path $SourcePath)) {
        throw "Source path not found: $SourcePath"
    }
    New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
    Copy-Item -Path "$SourcePath\*" -Destination $InstallPath -Recurse -Force
} else {
    # Build from source — look for project in script directory
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $projectFile = Join-Path $scriptDir "FoundryWebUI.csproj"

    if (-not (Test-Path $projectFile)) {
        throw "Project file not found at $projectFile. Use -SourcePath to specify a pre-built publish folder."
    }

    Write-Info "Building and publishing from $scriptDir"
    Push-Location $scriptDir
    try {
        dotnet publish -c Release -o $InstallPath --nologo 2>&1 | ForEach-Object { Write-Info $_ }
    } finally {
        Pop-Location
    }
}

# Verify published output
if ((Test-Path "$InstallPath\FoundryWebUI.dll") -and (Test-Path "$InstallPath\web.config")) {
    Write-Success "Application published to $InstallPath"
} else {
    throw "Publish failed: FoundryWebUI.dll or web.config not found in $InstallPath"
}

# ============================================================
# Step 6: Create IIS website
# ============================================================
Write-Step "Step 6: Creating IIS website"

Import-Module IISAdministration -ErrorAction SilentlyContinue
$appcmd = "$env:SystemRoot\System32\inetsrv\appcmd.exe"

# Stop Default Web Site if it exists and we're using port 80
if ($Port -eq 80) {
    $defaultSite = & $appcmd list site "Default Web Site" 2>$null
    if ($defaultSite) {
        Write-Info "Stopping Default Web Site (port 80 conflict)..."
        & $appcmd stop site "Default Web Site" 2>$null
        Write-Success "Default Web Site stopped"
    }
}

# Create or update app pool
$existingPool = & $appcmd list apppool $AppPoolName 2>$null
if ($existingPool) {
    Write-Info "Application pool '$AppPoolName' already exists, updating settings..."
} else {
    Write-Info "Creating application pool '$AppPoolName'..."
    & $appcmd add apppool /name:$AppPoolName | Out-Null
}

& $appcmd set apppool $AppPoolName /managedRuntimeVersion:"" | Out-Null
& $appcmd set apppool $AppPoolName /managedPipelineMode:"Integrated" | Out-Null
& $appcmd set apppool $AppPoolName /processModel.idleTimeout:"00:00:00" | Out-Null
& $appcmd set apppool $AppPoolName /startMode:"AlwaysRunning" | Out-Null
Write-Success "Application pool configured"

# Create or update website
$existingSite = & $appcmd list site $SiteName 2>$null
if ($existingSite) {
    Write-Info "Website '$SiteName' already exists, updating..."
    & $appcmd set site $SiteName /`[path=`'/`'`].physicalPath:$InstallPath | Out-Null
    & $appcmd set site $SiteName /`[path=`'/`'`].applicationPool:$AppPoolName | Out-Null
} else {
    Write-Info "Creating website '$SiteName' on port $Port..."
    & $appcmd add site /name:$SiteName /physicalPath:$InstallPath /bindings:"http/*:${Port}:" | Out-Null
    & $appcmd set app "$SiteName/" /applicationPool:$AppPoolName | Out-Null
}
Write-Success "Website '$SiteName' created on port $Port"

# ============================================================
# Step 7: Configure application settings
# ============================================================
Write-Step "Step 7: Configuring application settings"

$appSettingsPath = Join-Path $InstallPath "appsettings.json"

# Restore backed-up appsettings.json (preserves user customizations across updates)
if ($appSettingsBackup) {
    Write-Info "Restoring previous appsettings.json (preserving your customizations)"
    $appSettingsBackup | Set-Content $appSettingsPath -Encoding UTF8
    Write-Success "appsettings.json restored from backup"
}

if (Test-Path $appSettingsPath) {
    if ($FoundryEndpoint) {
        Write-Info "Setting Foundry endpoint to $FoundryEndpoint"
        $settings = Get-Content $appSettingsPath -Raw | ConvertFrom-Json
        $settings.LlmProviders.Foundry.Endpoint = $FoundryEndpoint
        $settings | ConvertTo-Json -Depth 10 | Set-Content $appSettingsPath -Encoding UTF8
        Write-Success "Foundry endpoint configured"
    } else {
        Write-Info "Foundry endpoint set to auto-detect (blank). Set explicitly if IIS cannot run 'foundry' CLI."
    }
} else {
    Write-Warning2 "appsettings.json not found at $appSettingsPath"
}

# ============================================================
# Step 8: Set permissions
# ============================================================
Write-Step "Step 8: Configuring file system permissions"

# Read/Execute on application folder
$acl = Get-Acl $InstallPath
$rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    "IIS AppPool\$AppPoolName", "ReadAndExecute", "ContainerInherit,ObjectInherit", "None", "Allow")
$acl.SetAccessRule($rule)
Set-Acl $InstallPath $acl
Write-Success "Read/Execute permission granted to IIS AppPool\$AppPoolName"

# Write access to logs folder
$logsPath = Join-Path $InstallPath "logs"
New-Item -ItemType Directory -Path $logsPath -Force | Out-Null
$acl = Get-Acl $logsPath
$rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    "IIS AppPool\$AppPoolName", "Modify", "ContainerInherit,ObjectInherit", "None", "Allow")
$acl.SetAccessRule($rule)
Set-Acl $logsPath $acl
Write-Success "Write permission granted on logs folder"

# ============================================================
# Step 9: Configure firewall
# ============================================================
if (-not $SkipFirewall) {
    Write-Step "Step 9: Configuring Windows Firewall"

    $existingRule = Get-NetFirewallRule -DisplayName "FoundryWebUI - HTTP Inbound" -ErrorAction SilentlyContinue
    if ($existingRule) {
        Write-Info "Firewall rule already exists, updating..."
        Set-NetFirewallRule -DisplayName "FoundryWebUI - HTTP Inbound" -LocalPort $Port
    } else {
        New-NetFirewallRule `
            -DisplayName "FoundryWebUI - HTTP Inbound" `
            -Direction Inbound `
            -Protocol TCP `
            -LocalPort $Port `
            -Action Allow `
            -Profile Domain, Private | Out-Null
    }
    Write-Success "Firewall rule configured for port $Port"
} else {
    Write-Info "Skipping firewall configuration (--SkipFirewall specified)"
}

# ============================================================
# Step 10: Start and verify
# ============================================================
Write-Step "Step 10: Starting and verifying deployment"

# Ensure site is started
& $appcmd start apppool $AppPoolName 2>$null
& $appcmd start site $SiteName 2>$null
Write-Success "Website started"

# Test the site
Start-Sleep -Seconds 2
try {
    $response = Invoke-WebRequest -Uri "http://localhost:$Port" -UseBasicParsing -TimeoutSec 10
    if ($response.StatusCode -eq 200) {
        Write-Success "Site is responding on http://localhost:$Port (HTTP 200)"
    } else {
        Write-Warning2 "Site responded with HTTP $($response.StatusCode)"
    }
} catch {
    Write-Warning2 "Could not reach http://localhost:$Port — $($_.Exception.Message)"
    Write-Info "The site may need a moment to start. Try browsing to http://localhost:$Port manually."
}

# Test API
try {
    $apiResponse = Invoke-RestMethod -Uri "http://localhost:$Port/api/status" -TimeoutSec 10
    foreach ($provider in $apiResponse) {
        if ($provider.isAvailable) {
            Write-Success "Provider '$($provider.provider)' is connected at $($provider.endpoint)"
        } else {
            Write-Warning2 "Provider '$($provider.provider)' is not available: $($provider.error)"
        }
    }
} catch {
    Write-Warning2 "Could not reach API endpoint — $($_.Exception.Message)"
}

# ============================================================
# Summary
# ============================================================
Write-Host ""
if ($isUpdate) {
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "  Update Complete!" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "  URL:          http://localhost:$Port" -ForegroundColor White
    Write-Host "  Install Path: $InstallPath" -ForegroundColor White
    Write-Host "  IIS Site:     $SiteName" -ForegroundColor White
    Write-Host ""
    Write-Host "  Your appsettings.json customizations have been preserved." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  To update again later:" -ForegroundColor Yellow
    Write-Host "    1. cd to your git repo folder" -ForegroundColor Yellow
    Write-Host "    2. git pull" -ForegroundColor Yellow
    Write-Host "    3. .\Install-FoundryWebUI.ps1" -ForegroundColor Yellow
    Write-Host ""
} else {
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "  Installation Complete!" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "  URL:          http://localhost:$Port" -ForegroundColor White
    Write-Host "  Install Path: $InstallPath" -ForegroundColor White
    Write-Host "  IIS Site:     $SiteName" -ForegroundColor White
    Write-Host "  App Pool:     $AppPoolName" -ForegroundColor White
    Write-Host ""
    Write-Host "  Next steps:" -ForegroundColor Yellow
    Write-Host "    1. Open http://localhost:$Port in a browser" -ForegroundColor Yellow
    Write-Host "    2. Check that provider status indicators are green" -ForegroundColor Yellow
    Write-Host "    3. Go to the Models page to download an LLM model" -ForegroundColor Yellow
    Write-Host "    4. Start chatting!" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  To update later:" -ForegroundColor Cyan
    Write-Host "    1. cd to your git repo folder" -ForegroundColor Cyan
    Write-Host "    2. git pull" -ForegroundColor Cyan
    Write-Host "    3. .\Install-FoundryWebUI.ps1" -ForegroundColor Cyan
    Write-Host ""
}
