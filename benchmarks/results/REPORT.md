# Sync benchmark

No old-vs-new measurements were taken.

The benchmark must use the MAIN and TARGET servers already configured for the app, over their real network path. A local SQL Server, Docker, and toxiproxy/netem were not used.

## What was checked

| Source | Result |
| --- | --- |
| `DatabaseSync.Worker/sync-config.json` | File does not exist. `SyncConfigStore` in `appsettings.json` points at `sync-config.json`, which is gitignored. |
| `DatabaseSync.Worker/sync-config.example.json` | Only connection file in the repo. |
| `DatabaseSync.Worker/appsettings.json` and `appsettings.Development.json` | Logging and the config file path only. No server, database, or password. |
| `DatabaseSync.Admin/appsettings.json` | Points at `..\sync-config.json`. No connection strings. |
| User secrets | None under the home directory. |
| Environment | No `DatabaseConnections__*`, `SYNC_CONFIG`, or SQL password variables. |

## Connections in the example file

Database name on all three is `RND_PMS_Sync`, which looks like a sync/test database. The servers and password are not usable:

| Name | Role | Server | Why it cannot be used |
| --- | --- | --- | --- |
| Main Cloud | MAIN | `YOUR_MAIN_IP` | Placeholder. DNS: `Name or service not known`. |
| Cloud Server | TARGET | `YOUR_CLOUD_IP` | Placeholder. DNS: `Name or service not known`. |
| Cloud Server 2 | TARGET | `YOUR_CLOUD2_IP` | Placeholder. DNS: `Name or service not known`. |

The password field on each connection is the placeholder `YOUR_PASSWORD`, not a real secret. It was not used to log in.

## Not done

Seeding, the 10k/100k/1M scenarios, repeats, and old-vs-new timings. Those need a real `sync-config.json` on the machine (or `SYNC_CONFIG`) whose servers resolve and whose databases are the test copies. `benchmarks/preflight.sh` checks that and stops without printing passwords.
