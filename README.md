# Sync_SQLDBs

Coditium centralized SQL Server database synchronisation (.NET 8 Worker + Admin UI).

Main → other servers sync for all tables, with Admin controls for connections, timing, and Run/Stop.

## Solution structure

```
DatabaseSync.sln
├── DatabaseSync.Worker          # Windows Service / BackgroundService
├── DatabaseSync.Core            # Interfaces, models, DTOs, enums
├── DatabaseSync.Infrastructure  # SQL connectivity + sync engine (Dapper / ADO.NET)
└── DatabaseSync.Admin           # Admin Web UI (connections, settings, Run/Stop)
```

Copy `DatabaseSync.Worker/sync-config.example.json` to `sync-config.json` and set real credentials (never commit passwords).

## Prerequisites

- .NET 8 SDK
- Windows (for Windows Service hosting)
- Network access (VPN) to the three SQL Server instances
- SQL Server / SQL Server Express (no SQL Agent or paid features required)

## Configure database connections

Edit `DatabaseSync.Worker/appsettings.json` (or override with User Secrets / environment variables):

```json
"DatabaseConnections": {
  "MainDatabase": {
    "Server": "MAIN_PRIVATE_IP",
    "Database": "YourMainDb",
    "IntegratedSecurity": false,
    "UserId": "sync_user",
    "Password": "",
    "ConnectionTimeoutSeconds": 15,
    "Encrypt": true,
    "TrustServerCertificate": true
  },
  "CloudDatabase": { "...": "..." },
  "LocalDatabase": { "...": "..." }
}
```

### SQL authentication (recommended for service accounts)

Leave `IntegratedSecurity` as `false` and set `UserId` / `Password`.

### Windows authentication

Set `"IntegratedSecurity": true` and leave `UserId` / `Password` empty.  
The Windows Service account must have SQL login rights on each server.

### Secure credentials (do not hardcode production passwords)

**Development – User Secrets:**

```powershell
cd DatabaseSync.Worker
dotnet user-secrets set "DatabaseConnections:MainDatabase:Password" "YOUR_PASSWORD"
dotnet user-secrets set "DatabaseConnections:CloudDatabase:Password" "YOUR_PASSWORD"
dotnet user-secrets set "DatabaseConnections:LocalDatabase:Password" "YOUR_PASSWORD"
```

**Production – environment variables:**

```
DatabaseConnections__MainDatabase__Password
DatabaseConnections__CloudDatabase__Password
DatabaseConnections__LocalDatabase__Password
```

## Sync settings

```json
"SyncSettings": {
  "SyncIntervalSeconds": 30,
  "EnableSynchronization": false,
  "RetryIntervalSeconds": 30,
  "MaximumRetryAttempts": 5,
  "EnableDetailedLogging": true
}
```

Keep `EnableSynchronization` set to `false` until the synchronisation engine is approved.

## Run locally

```powershell
cd D:\Dotnet\CoreCursor\SynchroniseDB
dotnet restore
dotnet build
dotnet run --project DatabaseSync.Worker
```

Or open `DatabaseSync.sln` in Visual Studio and press F5 (Worker project).

Logs are written to:

- Console
- `DatabaseSync.Worker/Logs/DatabaseSync-YYYYMMDD.log` (daily rolling, 30-day retention)

Stop with `Ctrl+C` (graceful shutdown).

## Publish for Windows Server

```powershell
cd D:\Dotnet\CoreCursor\SynchroniseDB
dotnet publish DatabaseSync.Worker\DatabaseSync.Worker.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -o .\publish\DatabaseSync
```

Copy the `publish\DatabaseSync` folder to the Main Cloud Server (for example `C:\Services\DatabaseSync`).

Update `appsettings.json` (or environment variables) on that server with the real private IPs and credentials.

## Install / manage Windows Service (PowerShell as Administrator)

```powershell
$publishPath = "C:\Services\DatabaseSync"
$exePath = Join-Path $publishPath "DatabaseSync.Worker.exe"

# Install
sc.exe create "Coditium.DatabaseSync" binPath= "`"$exePath`"" start= auto DisplayName= "Coditium Database Sync"
sc.exe description "Coditium.DatabaseSync" "Centralized SQL Server connectivity and synchronisation worker."

# Start
sc.exe start "Coditium.DatabaseSync"

# Status
sc.exe query "Coditium.DatabaseSync"

# Stop
sc.exe stop "Coditium.DatabaseSync"

# Uninstall (stop first)
sc.exe stop "Coditium.DatabaseSync"
sc.exe delete "Coditium.DatabaseSync"
```

Optional: set the service to run under a dedicated domain/service account if using Windows authentication.

## What this phase does

1. Starts with Windows / interactive host
2. Loads configuration
3. Tests connectivity to Main, Cloud, and Local SQL Servers every 30 seconds
4. Retries failed connections (configurable)
5. Logs results with Serilog
6. Survives individual connection failures without crashing
7. Shuts down gracefully

## What this phase does **not** do

- No data synchronisation
- No database schema changes
- No triggers
- No modifications to production databases
