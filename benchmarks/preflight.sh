#!/usr/bin/env bash
# Checks the repo's sync connections. Exits 2 when a benchmark must not start.
# Does not print passwords and does not start a local SQL Server.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CONFIG="${SYNC_CONFIG:-$ROOT/DatabaseSync.Worker/sync-config.json}"
EXAMPLE="$ROOT/DatabaseSync.Worker/sync-config.example.json"

if [[ ! -f "$CONFIG" ]]; then
  echo "BLOCKED: sync config not found at $CONFIG"
  echo "The only connection file in the repo is $EXAMPLE."
  echo "Its servers are YOUR_MAIN_IP, YOUR_CLOUD_IP, and YOUR_CLOUD2_IP."
  echo "Its password field is the placeholder YOUR_PASSWORD."
  echo "There is no sync-config.json, no user-secrets file, and no DatabaseConnections environment variables."
  echo "Those placeholder hosts do not resolve from this VM. Not starting a local SQL Server."
  exit 2
fi

python3 - "$CONFIG" << 'PY'
import json, socket, sys
path = sys.argv[1]
with open(path) as f:
    cfg = json.load(f)
connections = cfg.get("Connections") or []
if not connections:
    print(f"BLOCKED: {path} has no Connections.")
    sys.exit(2)
placeholders = {"", "YOUR_PASSWORD", "YOUR_MAIN_IP", "YOUR_CLOUD_IP", "YOUR_CLOUD2_IP", "CHANGEME"}
blocked = False
for c in connections:
    if c.get("IsEnabled") is False:
        continue
    name = c.get("Name") or c.get("Id") or "(unnamed)"
    server = (c.get("Server") or "").strip()
    database = (c.get("Database") or "").strip()
    password = c.get("Password") or ""
    role = "MAIN" if c.get("IsMain") else "TARGET"
    problems = []
    if not server or server in placeholders:
        problems.append(f"server is missing or a placeholder ({server or 'empty'})")
    if not database:
        problems.append("database name is empty")
    if c.get("IntegratedSecurity") is not True and (not password or password in placeholders):
        problems.append("password is empty or a placeholder")
    if problems:
        blocked = True
        print(f"BLOCKED: {role} '{name}' database '{database or '(none)'}': " + "; ".join(problems))
        continue
    try:
        socket.getaddrinfo(server.split(",")[0], 1433)
    except socket.gaierror as ex:
        blocked = True
        print(f"BLOCKED: {role} '{name}' server {server!r} database '{database}' does not resolve ({ex}).")
if blocked:
    print("Not starting a local SQL Server and not guessing other hosts.")
    sys.exit(2)
print(f"Connections in {path} look usable. Hosts resolve. Passwords were not printed.")
PY
