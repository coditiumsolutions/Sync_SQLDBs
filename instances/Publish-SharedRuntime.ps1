#Requires -Version 5.1
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$runtime = Join-Path $PSScriptRoot "_runtime"
$workerOut = Join-Path $runtime "worker"
$adminOut = Join-Path $runtime "admin"

Write-Host "Publishing shared Worker -> $workerOut"
dotnet publish (Join-Path $root "DatabaseSync.Worker\DatabaseSync.Worker.csproj") `
    -c $Configuration `
    -o $workerOut `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "Worker publish failed." }

Write-Host "Publishing shared Admin -> $adminOut"
dotnet publish (Join-Path $root "DatabaseSync.Admin\DatabaseSync.Admin.csproj") `
    -c $Configuration `
    -o $adminOut `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "Admin publish failed." }

Write-Host "Shared runtime ready."
Write-Host "  Worker: $workerOut\DatabaseSync.Worker.exe"
Write-Host "  Admin:  $adminOut\DatabaseSync.Admin.exe"
