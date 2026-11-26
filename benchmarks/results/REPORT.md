# Sync benchmark on the test servers

No local SQL Server. Payload bytes for `new` and `fallback` are the engine estimate. Payload bytes for `old` are rows read times the measured average row width, so a full-table bulk copy is not on the same ruler as the old figure. Every run below matched. Medians of 3 repeats.

Main Cloud did not accept TCP 1433. Cloud Server was the stand-in main and Cloud Server 2 was the only target. Both use database `RND_PMS_Sync`. Only schema `bench` was created for the runs, and it was dropped on both servers afterwards.

`RND_PMS_Sync` was missing on Cloud Server. This run created that database there. It is a database, not a table. It was not dropped. After the run it contains 48 application tables (about 844 rows) with the same names as Cloud Server 2. This session did not insert those rows and did not drop or alter them. Schema `bench` is not among them.

## Threshold

`BulkFallbackEnabled` defaults to on. After the range checksum, the engine switches to one `SqlBulkCopy` of the whole table and a single `MERGE` when either of these is true:

- dirty ranges cover at least 15,000 rows (`BulkFallbackMismatchedRows`)
- dirty ranges cover at least 8,000 rows and 20% of the table (`BulkFallbackFractionMinRows`, `BulkFallbackChangedFraction`)

A matching checksum stays on the per-key path, so a no-op does not bulk-copy. The optional `BulkFallbackSmallTableRows` rule (any change on a small table) is 0. A measured full copy of the 10k tables was slower than the per-key path, so that rule is off. Newer target rows are still selected with the same timestamp rules and backed up before the merge.

The 100k 1% case stays under both bars (about 3,500 changed rows). The 100k 10% case crosses 15,000 dirty rows, which is the case that used to take 67 s against a 35 s full copy.

## 10k

| Scenario | Old | New | Fallback |
| --- | ---: | ---: | ---: |
| steady | 16.3 s / 0.48 MB | 6.1 s / 288 B | 6.5 s / 288 B |
| 1% | 17.3 s / 0.48 MB | 23.1 s / 138 KB | 18.3 s / 138 KB |
| 10% | 16.5 s / 0.49 MB | 19.6 s / 388 KB | 19.8 s / 388 KB |
| conflicts | 16.6 s / 0.47 MB | 16.5 s / 141 KB | 16.9 s / 141 KB |

Conflicts logged 1,020 of 1,020 for `new` and `fallback`. At 10k the dirty set stays under the threshold, so fallback and the per-key path send the same bytes. The no-op stays near 6 s.

## 100k

| Scenario | Old | New | Fallback |
| --- | ---: | ---: | ---: |
| steady | 27.5 s / 4.3 MB | 5.7 s / 2.1 KB | 5.9 s / 2.1 KB |
| 1% | 26.9 s / 4.4 MB | 20.7 s / 365 KB | 20.6 s / 365 KB |
| 10% | 28.4 s / 4.4 MB | 42.5 s / 3.0 MB | 34.1 s / 9.0 MB |
| conflicts | 26.6 s / 4.3 MB | 17.3 s / 192 KB | 17.5 s / 192 KB |

Conflicts logged 1,520 of 1,520 for `new` and `fallback`. The 1% and conflict cases stay on the per-key path. The 10% case is the one that switches: 34.1 s versus 42.5 s for the per-key engine and 28.4 s for the full copy. It closes most of the old gap (previously 67 s versus 35 s) and is still a few seconds behind a full copy. The 9.0 MB figure is the engine's column-width estimate for the whole table, not the measured byte width used for the old engine.

One million rows were not run.
