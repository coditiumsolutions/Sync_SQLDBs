# Sync benchmark on the test servers

No local SQL Server and no latency proxy. Bytes labeled payload are the engine estimate (new) or rows read times the measured average row width (old). SQL network counters are instance-wide and can include other traffic, so they are secondary.

Main Cloud `172.20.228.2` did not accept TCP 1433 from this VM (timed out). Cloud Server (SQL Server 2022 Express) was the stand-in main and Cloud Server 2 (SQL Server 2025 Standard Developer) was the only target. Both use database `RND_PMS_Sync`.

Only schema `bench` was created (`bench.Customer`, `bench.OrderHeader`, `bench.OrderLine`, `bench.AuditHeap`, `bench.Nums`). It was dropped on both servers after the run.

`bench.AuditHeap` has no primary key and must stay empty on the target. Conflicts are 20 newer customers plus up to 1,500 newer order lines, including deletes.

The shape suggested by [BTK_ExcelSearch](https://github.com/Rayder-23/BTK_ExcelSearch) is to bulk-copy a `(primary key, SHA-256)` list into a staging table and classify it with one join, because moving full rows is the expensive part. On these servers that hash was the expensive part instead: building SHA-256 for every narrow row took longer than the old full copy, and the hash list was about the same size as the rows. Equal ranges now stop after one grouped checksum on each server. A mismatched range is read once per server as primary key plus an 8-byte checksum pair, classified in memory, and only those keys are bulk-fetched and merged. Apply batches of about 4,000 rows only avoid lock escalation. Tables without a single integer key still use the staged SHA-256 join. Interval cycles can skip unchanged source rows with an `UpdatedAt` or `rowversion` watermark, with a full compare every sixth cycle so deletes are not lost. This benchmark's cycles are manual, so they do not use the watermark. One million rows were not scheduled; that size is included below only if a run named `1m` finished.

## Before the staged-hash change (10k, 3 repeats)

The previous engine compared rows with many round trips and then applied differences in small batches. Medians from that run, kept as the baseline:

| Scenario | Old time | New time | Old payload | New payload |
| --- | ---: | ---: | ---: | ---: |
| initial | 20.3 s | 17.0 s | 0.48 MB | 1.20 MB |
| steady | 19.7 s | 38.6 s | 0.48 MB | 0.48 MB |
| delta-1pct | 19.8 s | 56.7 s | 0.48 MB | 0.51 MB |
| delta-10pct | 20.5 s | 88.4 s | 0.49 MB | 0.74 MB |
| conflicts | 19.9 s | (run stopped) | 0.47 MB | (run stopped) |

## After, 10k, 3 repeats

Medians. Payload for the new engine is its own estimate (hash bytes plus rows actually sent). The old payload is rows read times the measured average row width, so the initial-load figures are not the same ruler.

| Scenario | Old time | New time | Old payload | New payload | Match | Conflicts logged |
| --- | ---: | ---: | ---: | ---: | --- | ---: |
| initial | 19.9 s | 22.6 s | 0.48 MB | 1.20 MB | yes | 0 |
| steady | 19.9 s | 7.7 s | 0.48 MB | 288 B | yes | 0 |
| delta-1pct | 19.7 s | 22.2 s | 0.48 MB | 138 KB | yes | 0 |
| delta-10pct | 20.0 s | 24.1 s | 0.49 MB | 388 KB | yes | 0 |
| conflicts | 19.9 s | 20.2 s | 0.47 MB | 141 KB | yes | 1020 of 1020 |

No-op is faster and ships almost nothing. A 1% or 10% change ships fewer bytes than the full copy and still loses a few seconds, because each of the three tables still pays several round trips at about 170 ms. Conflicts are flagged (20 newer customers and 1,000 newer lines, including deletes) and main still wins. One million rows were not run.

## After, 100k, 3 repeats

| Size | Scenario | Engine | Median time | Spread | Payload bytes | Rows read | Rows written | Source SQL CPU | Match |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| 100k | initial | old | 36599 ms | 7470 ms | 4.3 MB | 125000 | 125000 | 421 ms | yes |
| 100k | initial | new | 24238 ms | 439 ms | 11.1 MB | 125000 | 125000 | 558 ms | yes |
| 100k | steady | old | 40261 ms | 8324 ms | 4.3 MB | 125000 | 125000 | 93 ms | yes |
| 100k | steady | new | 7870 ms | 752 ms | 2.1 KB | 125000 | 0 | 31 ms | yes |
| 100k | delta-1pct | old | 34375 ms | 731 ms | 4.4 MB | 125250 | 126250 | 570 ms | yes |
| 100k | delta-1pct | new | 24468 ms | 1266 ms | 364.8 KB | 125250 | 3500 | 135 ms | yes |
| 100k | delta-10pct | old | 35157 ms | 26620 ms | 4.4 MB | 127500 | 137500 | 47 ms | yes |
| 100k | delta-10pct | new | 67086 ms | 55272 ms | 3.0 MB | 127500 | 35000 | 1261 ms | yes |
| 100k | conflicts | old | 33212 ms | 133 ms | 4.3 MB | 124813 | 125000 | 697 ms | yes |
| 100k | conflicts | new | 20965 ms | 1061 ms | 191.7 KB | 124813 | 1520 | 652 ms | yes |

## Conflicts

| Size | Engine | Median log lines | JSON files | JSONL files | Expected |
| --- | --- | ---: | ---: | ---: | ---: |
| 100k | old | 0 | 0 | 0 | 1520 |
| 100k | new | 1520 | 1 | 1 | 1520 |

## Conclusion

- initial: old median 36599 ms / 4.3 MB, new median 24238 ms / 11.1 MB.
- steady: old median 40261 ms / 4.3 MB, new median 7870 ms / 2.1 KB.
- delta-1pct: old median 34375 ms / 4.4 MB, new median 24468 ms / 364.8 KB.
- delta-10pct: old median 35157 ms / 4.4 MB, new median 67086 ms / 3.0 MB.
- conflicts: old median 33212 ms / 4.3 MB, new median 20965 ms / 191.7 KB.

At 100k the new engine is faster on the initial load, the no-op (7.9 s and 2.1 KB versus 40 s and 4.3 MB), a 1% change, and the conflict case (1,520 of 1,520 newer rows flagged, main still applied). A 10% change still sends fewer bytes (3.0 MB versus 4.4 MB) but the median was slower (67 s versus 35 s), with a wide spread. One million rows were not run: the 100k 10% case was already the slow one, and a million-row copy would mostly repeat it. `bench` was dropped on both servers. Main Cloud stayed unreachable, so Cloud Server stood in as main.
