# Multi-product sync (one at a time)

One Admin/Worker process = **one product**.  
Products share a **single port** (`shared-port.json`, default **5088**). Starting a product stops any other running product.

```
instances/
  shared-port.json   # shared Admin URL / port
  _runtime/          # shared published Admin + Worker
  PMS/
  POS/
  Restaurant/
  Billing/
```

## Quick start

From the repo root (`SynchroniseDB`):

```powershell
# 1) Publish shared binaries once
.\instances\Publish-SharedRuntime.ps1

# 2) Create products (or use Add product in the Admin UI)
.\instances\New-SyncInstance.ps1 -Name PMS -Database RND_PMS_Sync
.\instances\New-SyncInstance.ps1 -Name POS -Database YourPosDb

# 3) Edit real IPs / passwords (never commit these)
notepad .\instances\PMS\sync-config.json

# 4) Start one product (stops any other first)
.\instances\Start-SyncInstance.ps1 -Name PMS

# 5) Open Admin
# http://127.0.0.1:5088
# Login: admin / Admin@123
```

Or use **Products** in the Admin UI: Add / Edit / Start / Stop.

## Rules

| Item | Per product? |
|------|----------------|
| `sync-config.json` (connections + tables) | Yes |
| `management.db` (users, history) | Yes |
| Admin URL / port | Shared (one product running) |
| Published binaries (`_runtime`) | Shared |

Do **not** point two products at the same Main/target pair unless you intend that.
