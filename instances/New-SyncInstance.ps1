#Requires -Version 5.1
param(
    [Parameter(Mandatory = $true)]
    [string]$Name,

    [string]$Database = "YourDatabase",

    [switch]$ForceConfig
)

$ErrorActionPreference = "Stop"
$Name = $Name.Trim()
if ($Name -notmatch '^[A-Za-z0-9_\-]+$') {
    throw "Name may only contain letters, numbers, underscore, and hyphen."
}

$sharedPortPath = Join-Path $PSScriptRoot "shared-port.json"
$port = 5088
if (Test-Path $sharedPortPath) {
    $shared = Get-Content $sharedPortPath -Raw | ConvertFrom-Json
    if ($shared.port) { $port = [int]$shared.port }
}

$instanceRoot = Join-Path $PSScriptRoot $Name
$configPath = Join-Path $instanceRoot "sync-config.json"
$examplePath = Join-Path $instanceRoot "sync-config.example.json"
$metaPath = Join-Path $instanceRoot "instance.json"
$repoExample = Join-Path (Split-Path -Parent $PSScriptRoot) "DatabaseSync.Worker\sync-config.example.json"

New-Item -ItemType Directory -Force -Path $instanceRoot | Out-Null

$meta = [ordered]@{
    name         = $Name
    port         = $port
    adminUrl     = "http://127.0.0.1:$port"
    databaseHint = $Database
    configPath   = $configPath
    createdAtUtc = (Get-Date).ToUniversalTime().ToString("o")
}
$meta | ConvertTo-Json | Set-Content -Path $metaPath -Encoding UTF8

if (-not (Test-Path $repoExample)) {
    throw "Missing template: $repoExample"
}

Copy-Item $repoExample $examplePath -Force

if ((-not (Test-Path $configPath)) -or $ForceConfig) {
    $json = Get-Content $repoExample -Raw | ConvertFrom-Json
    foreach ($c in $json.Connections) {
        $c.Database = $Database
        $c.ApplicationName = "Coditium.DatabaseSync.$Name"
        $c.Name = if ($c.IsMain) { "$Name Main" } else { "$Name Target" }
        if ($c.Id -eq "main-server") { $c.Id = "$($Name.ToLowerInvariant())-main" }
        elseif ($c.Id -eq "cloud-server-1") { $c.Id = "$($Name.ToLowerInvariant())-cloud-1" }
        elseif ($c.Id -eq "cloud-server-2") { $c.Id = "$($Name.ToLowerInvariant())-cloud-2" }
    }
    $json | ConvertTo-Json -Depth 20 | Set-Content -Path $configPath -Encoding UTF8
    Write-Host "Wrote $configPath (edit IPs and passwords)."
}
else {
    Write-Host "Kept existing $configPath (use -ForceConfig to overwrite)."
}

$startLocal = Join-Path $instanceRoot "start.ps1"
$stopLocal = Join-Path $instanceRoot "stop.ps1"
@"
# Start $Name sync instance (stops any other product first — shared port)
& "`$PSScriptRoot\..\Start-SyncInstance.ps1" -Name "$Name"
"@ | Set-Content $startLocal -Encoding UTF8

@"
# Stop $Name sync instance
& "`$PSScriptRoot\..\Stop-SyncInstance.ps1" -Name "$Name"
"@ | Set-Content $stopLocal -Encoding UTF8

Write-Host ""
Write-Host "Instance ready: $instanceRoot"
Write-Host "  Shared Admin URL: http://127.0.0.1:$port"
Write-Host "  Config:           $configPath"
Write-Host "Next:"
Write-Host "  1. Edit sync-config.json connections for $Name"
Write-Host "  2. .\instances\Publish-SharedRuntime.ps1   # once"
Write-Host "  3. .\instances\Start-SyncInstance.ps1 -Name $Name"
Write-Host "Only one product can run at a time on port $port."
