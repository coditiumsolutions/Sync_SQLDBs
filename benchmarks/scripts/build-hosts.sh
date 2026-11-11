#!/usr/bin/env bash
# Builds the new differential host from this branch and the old whole-table host from main.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
export PATH="${HOME}/.dotnet:${PATH}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

echo "Building new sync host"
dotnet build "${ROOT}/benchmarks/NewSyncHost/NewSyncHost.csproj" -c Release

OLD="${ROOT}/benchmarks/.build/old-src"
rm -rf "${OLD}"
mkdir -p "${OLD}"
git -C "${ROOT}" archive main | tar -x -C "${OLD}"
rm -rf "${OLD}/OldSyncHost"
cp -a "${ROOT}/benchmarks/old-host" "${OLD}/OldSyncHost"
echo "Building old sync host from $(git -C "${ROOT}" rev-parse main)"
dotnet build "${OLD}/OldSyncHost/OldSyncHost.csproj" -c Release
echo "Hosts ready"
