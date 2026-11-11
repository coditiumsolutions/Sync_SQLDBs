# Sync benchmark

This measures the whole-table copy (`main`) against the differential engine on the real MAIN and TARGET servers from `sync-config.json`.

Do not start a local SQL Server, Docker, or a latency proxy for this benchmark. The network path is the one in the config file.

```bash
benchmarks/preflight.sh
```

The script reads `DatabaseSync.Worker/sync-config.json` (gitignored) or `SYNC_CONFIG`. It checks that each enabled connection has a server, a database, and a non-placeholder password, then tries to open SQL Server. If any check fails it stops and names the connection. It does not print passwords and it does not fall back to a local instance.

`RND_PMS_Sync` in the example file is a sync/test database name. The example servers and password are placeholders, so that file is not usable.

When preflight passes, seed only `bench_` tables inside those databases, run old vs new (initial, steady, 1%, 10%, conflicts; 3 repeats), and write `benchmarks/results/REPORT.md` without credentials.
