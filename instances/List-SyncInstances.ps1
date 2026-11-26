#Requires -Version 5.1
$ErrorActionPreference = "Stop"

$sharedPort = 5088
$sharedPath = Join-Path $PSScriptRoot "shared-port.json"
if (Test-Path $sharedPath) {
    $shared = Get-Content $sharedPath -Raw | ConvertFrom-Json
    if ($shared.port) { $sharedPort = [int]$shared.port }
}

$active = $null
$activePath = Join-Path $PSScriptRoot "active-product.txt"
if (Test-Path $activePath) {
    $active = (Get-Content $activePath -Raw).Trim()
}

$rows = @()
Get-ChildItem $PSScriptRoot -Directory |
    Where-Object { $_.Name -notin @('_runtime') -and (Test-Path (Join-Path $_.FullName 'instance.json')) } |
    ForEach-Object {
        $meta = Get-Content (Join-Path $_.FullName 'instance.json') -Raw | ConvertFrom-Json
        $pidDir = Join-Path $_.FullName '.run'
        $adminPid = $null
        $workerPid = $null
        $adminFile = Join-Path $pidDir 'admin.pid'
        $workerFile = Join-Path $pidDir 'worker.pid'
        if (Test-Path $adminFile) {
            $id = 0
            [void][int]::TryParse((Get-Content $adminFile | Select-Object -First 1), [ref]$id)
            if ($id -gt 0 -and (Get-Process -Id $id -ErrorAction SilentlyContinue)) { $adminPid = $id }
        }
        if (Test-Path $workerFile) {
            $id = 0
            [void][int]::TryParse((Get-Content $workerFile | Select-Object -First 1), [ref]$id)
            if ($id -gt 0 -and (Get-Process -Id $id -ErrorAction SilentlyContinue)) { $workerPid = $id }
        }
        $rows += [pscustomobject]@{
            Name     = $meta.name
            Port     = $sharedPort
            Database = $meta.databaseHint
            Active   = if ($active -and $meta.name -eq $active) { "yes" } else { "" }
            Admin    = if ($adminPid) { "UP ($adminPid)" } else { "down" }
            Worker   = if ($workerPid) { "UP ($workerPid)" } else { "down" }
        }
    }

Write-Host "Shared port: $sharedPort  (http://127.0.0.1:$sharedPort) — one product at a time"
if ($rows.Count -eq 0) {
    Write-Host "No instances yet. Use Add product in Admin or New-SyncInstance.ps1"
}
else {
    $rows | Format-Table -AutoSize
}
