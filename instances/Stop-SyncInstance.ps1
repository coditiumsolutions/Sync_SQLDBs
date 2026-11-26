#Requires -Version 5.1
param(
    [Parameter(Mandatory = $true)]
    [string]$Name
)

$ErrorActionPreference = "Stop"
$Name = $Name.Trim()
$instanceRoot = Join-Path $PSScriptRoot $Name
$pidDir = Join-Path $instanceRoot ".run"
$workerPidFile = Join-Path $pidDir "worker.pid"
$adminPidFile = Join-Path $pidDir "admin.pid"

function Stop-Tracked([string]$file, [string]$label) {
    if (-not (Test-Path $file)) {
        Write-Host "${label}: not tracked"
        return
    }
    $procId = 0
    [void][int]::TryParse((Get-Content $file -ErrorAction SilentlyContinue | Select-Object -First 1), [ref]$procId)
    if ($procId -gt 0) {
        $proc = Get-Process -Id $procId -ErrorAction SilentlyContinue
        if ($proc) {
            Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue
            Write-Host "${label}: stopped pid $procId"
        }
        else {
            Write-Host "${label}: pid $procId not running"
        }
    }
    Remove-Item $file -Force -ErrorAction SilentlyContinue
}

if (-not (Test-Path $instanceRoot)) {
    throw "Unknown instance '$Name'."
}

Stop-Tracked $adminPidFile "Admin"
Stop-Tracked $workerPidFile "Worker"
Write-Host "$Name stopped."
