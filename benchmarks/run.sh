#!/usr/bin/env bash
# Builds both sync hosts and runs the benchmark against DatabaseSync.Worker/sync-config.json.
# That file is gitignored. The driver never prints the password.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export PATH="${HOME}/.dotnet:${PATH}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
bash "${ROOT}/benchmarks/scripts/build-hosts.sh"
dotnet run --project "${ROOT}/benchmarks/BenchmarkDriver/BenchmarkDriver.csproj" -c Release -- "$@"
