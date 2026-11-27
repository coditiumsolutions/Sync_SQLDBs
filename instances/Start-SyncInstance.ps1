#Requires -Version 5.1
param(
    [Parameter(Mandatory = $true)]
    [string]$Name
)

$ErrorActionPreference = "Stop"
$Name = $Name.Trim()
$instanceRoot = Join-Path $PSScriptRoot $Name
$metaPath = Join-Path $instanceRoot "instance.json"
$configPath = Join-Path $instanceRoot "sync-config.json"
$runtime = Join-Path $PSScriptRoot "_runtime"
$workerExe = Join-Path $runtime "worker\DatabaseSync.Worker.exe"
$adminExe = Join-Path $runtime "admin\DatabaseSync.Admin.exe"
$pidDir = Join-Path $instanceRoot ".run"
$workerPidFile = Join-Path $pidDir "worker.pid"
$adminPidFile = Join-Path $pidDir "admin.pid"
$workerLog = Join-Path $pidDir "worker.out.log"
$adminLog = Join-Path $pidDir "admin.out.log"
$sharedPortPath = Join-Path $PSScriptRoot "shared-port.json"

if (-not (Test-Path $metaPath)) {
    throw "Unknown instance '$Name'. Run New-SyncInstance.ps1 first."
}
if (-not (Test-Path $configPath)) {
    throw "Missing $configPath"
}
if (-not (Test-Path $workerExe) -or -not (Test-Path $adminExe)) {
    Write-Host "Shared runtime missing. Publishing..."
    & (Join-Path $PSScriptRoot "Publish-SharedRuntime.ps1")
}

# Shared port: only one product at a time
$port = 5088
if (Test-Path $sharedPortPath) {
    $shared = Get-Content $sharedPortPath -Raw | ConvertFrom-Json
    if ($shared.port) { $port = [int]$shared.port }
}

$meta = Get-Content $metaPath -Raw | ConvertFrom-Json
$meta.port = $port
$meta.adminUrl = "http://127.0.0.1:$port"
$meta | ConvertTo-Json | Set-Content -Path $metaPath -Encoding UTF8

$url = "http://127.0.0.1:$port"
$mgmtDb = Join-Path $instanceRoot "management.db"
# Synchronization projects are shared across all products (do not vanish on Active Database switch)
$syncProjectsDb = Join-Path $PSScriptRoot "synchronization.db"

function Stop-Tracked([string]$file, [string]$label) {
    if (-not (Test-Path $file)) { return }
    $procId = 0
    [void][int]::TryParse((Get-Content $file -ErrorAction SilentlyContinue | Select-Object -First 1), [ref]$procId)
    if ($procId -gt 0) {
        $proc = Get-Process -Id $procId -ErrorAction SilentlyContinue
        if ($proc) {
            Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue
            Write-Host "Stopped $label pid $procId"
        }
    }
    Remove-Item $file -Force -ErrorAction SilentlyContinue
}

# Stop every product instance so the shared port is free
Write-Host "Stopping any running products (single-port mode)..."
Get-ChildItem $PSScriptRoot -Directory |
    Where-Object { $_.Name -ne '_runtime' -and (Test-Path (Join-Path $_.FullName 'instance.json')) } |
    ForEach-Object {
        $run = Join-Path $_.FullName '.run'
        Stop-Tracked (Join-Path $run 'admin.pid') "$($_.Name) Admin"
        Stop-Tracked (Join-Path $run 'worker.pid') "$($_.Name) Worker"
    }

# Free shared port if something else still holds it
try {
    $listeners = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
    foreach ($l in $listeners) {
        if ($l.OwningProcess -gt 0) {
            Stop-Process -Id $l.OwningProcess -Force -ErrorAction SilentlyContinue
            Write-Host "Freed port $port (pid $($l.OwningProcess))"
        }
    }
} catch { }

Start-Sleep -Seconds 1

New-Item -ItemType Directory -Force -Path $pidDir | Out-Null

function Get-RunningPid([string]$file) {
    if (-not (Test-Path $file)) { return $null }
    $procId = 0
    [void][int]::TryParse((Get-Content $file -ErrorAction SilentlyContinue | Select-Object -First 1), [ref]$procId)
    if ($procId -le 0) { return $null }
    if (Get-Process -Id $procId -ErrorAction SilentlyContinue) { return $procId }
    return $null
}

function Start-TrackedProcess {
    param(
        [string]$Exe,
        [string]$WorkDir,
        [string]$PidFile,
        [string]$StdOut,
        [hashtable]$EnvVars
    )

    $existing = Get-RunningPid $PidFile
    if ($existing) {
        Write-Host "Already running: $Exe (pid $existing)"
        return $existing
    }

    foreach ($key in $EnvVars.Keys) {
        Set-Item -Path "Env:$key" -Value $EnvVars[$key]
    }

    $proc = Start-Process -FilePath $Exe `
        -WorkingDirectory $WorkDir `
        -PassThru `
        -WindowStyle Hidden `
        -RedirectStandardOutput $StdOut `
        -RedirectStandardError ($StdOut -replace '\.out\.log$', '.err.log')

    Set-Content -Path $PidFile -Value $proc.Id -Encoding ASCII
    Write-Host "Started $([IO.Path]::GetFileName($Exe)) pid $($proc.Id)"
    return $proc.Id
}

$envMap = @{
    SyncConfigStore__FilePath     = $configPath
    ManagementDb__FilePath        = $mgmtDb
    SynchronizationDb__FilePath   = $syncProjectsDb
    ASPNETCORE_URLS               = $url
    ASPNETCORE_ENVIRONMENT        = "Production"
}

# Mark active product
Set-Content -Path (Join-Path $PSScriptRoot "active-product.txt") -Value $Name -Encoding UTF8

Start-TrackedProcess -Exe $workerExe -WorkDir (Split-Path $workerExe) -PidFile $workerPidFile -StdOut $workerLog -EnvVars $envMap | Out-Null
Start-TrackedProcess -Exe $adminExe -WorkDir (Split-Path $adminExe) -PidFile $adminPidFile -StdOut $adminLog -EnvVars $envMap | Out-Null

Write-Host ""
Write-Host "$Name is running (shared port $port)."
Write-Host "  Admin:  $url"
Write-Host "  Config: $configPath"
Write-Host "  Login:  admin / Admin@123"
