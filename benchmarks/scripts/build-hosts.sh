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
# Whole-table copy, the mainline parent of the differential merge.
OLD_REV=982e65b22f85fecfe94b51dbbc885a28688b3ef8
git -C "${ROOT}" archive "${OLD_REV}" | tar -x -C "${OLD}"
rm -rf "${OLD}/OldSyncHost"
cp -a "${ROOT}/benchmarks/old-host" "${OLD}/OldSyncHost"
echo "Building old sync host from ${OLD_REV}"
dotnet build "${OLD}/OldSyncHost/OldSyncHost.csproj" -c Release
echo "Hosts ready"
