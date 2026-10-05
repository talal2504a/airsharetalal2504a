# AirShare

**Live site:** https://yoursite.tryasp.net

Share files and text over the same WiFi. **No login, no signup.**

Upload a file or text from any device → it instantly appears on every other device on the same network → it deletes itself after expiry.

---

## Features

- **No authentication**: no accounts, no signup
- **IP-based sharing**: same WiFi = same public IP = same list
- **Auto refresh**: the list polls every 3 seconds
- **Auto delete**: 10 / 15 / 30 / 60 minute expiry
- **Text sharing**: WiFi passwords, OTPs, notes (up to 20,000 chars)
- **Cross-network codes**: download from another network using a 6-digit code
- **Device name + uploader IP** shown in the list
- **Drag & drop** with progress bar
- **Light / dark theme**: dark by default, saved in localStorage
- **Responsive**: mobile-first, 44px+ tap targets

---

## Tech stack

| Layer | Technology |
|---|---|
| Backend | ASP.NET Core 8 Web API (C#) |
| ORM | Dapper (fully parameterized SQL) |
| Database | MSSQL (SQL Server) |
| Frontend | Vanilla HTML / CSS / JS (no framework) |
| Cleanup | `BackgroundService` + `PeriodicTimer` |
| Rate limiting | Built-in `AddRateLimiter` |
| Hosting | MonsterASP.net (IIS, Web Deploy) |

---

## Project structure

```
share/
├── AirShare.csproj
├── Program.cs                  DI, size limits, rate limiter, forwarded headers
├── web.config                  IIS config + maxAllowedContentLength
├── appsettings.json
├── Controllers/ShareController.cs   6 API endpoints + IP access control
├── Models/                     Share, UploadResult
├── Data/ShareRepository.cs     Parameterized Dapper queries
├── Services/
│   ├── ClientIpService.cs      IP normalization (IPv4 as-is, IPv6 /48)
│   ├── CodeGenerator.cs        Crypto-secure 6-digit codes
│   ├── FileStorageService.cs   GUID naming, path traversal guard
│   └── CleanupService.cs       60s expiry sweep
├── wwwroot/                    index.html, style.css, app.js, favicon.svg
├── App_Data/uploads/           User files (never served directly)
└── Database/create_tables.sql  Schema + indexes (idempotent)
```

---

## How it works

1. Client uploads via `POST /api/upload`.
2. The server resolves the real client IP (`X-Forwarded-For` via `UseForwardedHeaders`).
3. `ClientIpService.Normalize()` cleans the IP.
4. The row is saved with its `NetworkIp`.
5. Other devices call `/api/list`; matching IP → they see the items.
6. The frontend polls every 3 seconds.
7. `CleanupService` deletes expired rows and files every 60 seconds.

### IP grouping rules

| Protocol | Rule |
|---|---|
| IPv4 | As-is |
| IPv4-mapped IPv6 | Unwrapped first (`::ffff:1.2.3.4` → `1.2.3.4`) |
| IPv6 | `/48` prefix, so all LAN devices share one bucket |

> **HTTPS is required.** On plain HTTP some networks prefer IPv6, causing an IPv4/IPv6 mismatch between devices. Enabling SSL aligns them.

---

## Local setup

**Prerequisites:** .NET 8 SDK, SQL Server (LocalDB / Express / full)

```bash
# 1. Create database
sqlcmd -S "(localdb)\MSSQLLocalDB" -Q "CREATE DATABASE AirShare;"

# 2. Create tables
sqlcmd -S "(localdb)\MSSQLLocalDB" -d AirShare -i Database\create_tables.sql

# 3. Set the connection string in appsettings.json
#    "Server=(localdb)\\MSSQLLocalDB;Database=AirShare;Trusted_Connection=True;TrustServerCertificate=True"

# 4. Run
dotnet restore
dotnet run
```

Open `https://localhost:7001` (or whichever port is shown).

---

## API reference

| Method | Route | Description | Rate limit |
|---|---|---|---|
| POST | `/api/upload` | Upload file or text | 10/min/IP |
| GET | `/api/list` | Active shares for caller's IP | none |
| GET | `/api/download/{id}` | Download file (same IP) | 60/min/IP |
| GET | `/api/download-by-code/{code}` | Cross-network download | 60/min/IP |
| DELETE | `/api/share/{id}` | Manual delete (same IP) | none |
| GET | `/api/text/{id}` | Text content (same IP) | none |

### Upload request

```
POST /api/upload
Content-Type: multipart/form-data

file          (optional) the file
text          (optional) text content
expiryMinutes (optional) 10 | 15 | 30 | 60
uploaderName  (optional) display name
```

Send **either a file or text, not both**.

### List response

```json
{
  "networkIp": "203.0.113.42",
  "serverTimeUtc": "2026-10-05T10:58:41Z",
  "items": [
    {
      "id": 6,
      "type": "text",
      "fileName": "hello.txt",
      "fileSize": 5,
      "textPreview": "hello",
      "code": "162710",
      "uploaderName": "Rahul",
      "networkIp": "203.0.113.42",
      "createdAtUtc": "2026-10-05T10:10:33Z",
      "expiresAtUtc": "2026-10-05T10:25:33Z",
      "downloadCount": 0
    }
  ]
}
```

---

## Database

### Table: `dbo.Shares`

| Column | Type | Notes |
|---|---|---|
| `Id` | `INT IDENTITY` | PK |
| `NetworkIp` | `VARCHAR(45)` | Grouping key |
| `UploaderName` | `VARCHAR(50)` | Device or user name |
| `ShareType` | `VARCHAR(10)` | `CHECK IN ('file','text')` |
| `FileName` | `NVARCHAR(255)` | Original name (display only) |
| `StoredName` | `VARCHAR(100)` | GUID name on disk |
| `TextContent` | `NVARCHAR(MAX)` | Text share content |
| `FileSize` | `BIGINT` | Bytes |
| `Code` | `VARCHAR(6)` | Digits only, unique filtered index |
| `CreatedAt` | `DATETIME2(3)` | `DEFAULT SYSUTCDATETIME()` |
| `ExpiresAt` | `DATETIME2(3)` | NOT NULL |
| `DownloadCount` | `INT` | `DEFAULT 0` |

### Indexes

| Index | Purpose |
|---|---|
| `IX_Shares_NetworkIp_ExpiresAt` | List query |
| `IX_Shares_ExpiresAt` | Cleanup query |
| `UX_Shares_Code` | Unique + filtered, code lookup |

> Filtered indexes require `SET QUOTED_IDENTIFIER ON` and `SET ANSI_NULLS ON` (already set in the script), otherwise error 1934 occurs.

---

## Security

| Layer | Protection |
|---|---|
| SQL injection | All queries parameterized |
| Path traversal | `StoredName` regex + full-path recheck + root prefix check |
| Filenames on disk | `Guid.NewGuid()` + sanitized extension only |
| Dangerous uploads | 21 extensions blocked (`.exe .bat .cmd .ps1 .js .vbs .msi` …) |
| IP spoofing | Client-supplied IPs are never trusted |
| Cross-network access | `403` if caller IP doesn't match the share owner |
| Brute force | 10 uploads/min, 60 downloads/min per IP |
| Codes | `RandomNumberGenerator` (crypto-secure) |
| XSS | Frontend uses `textContent`; `innerHTML` only for static SVG |
| Downloads | `Content-Disposition: attachment` |
| Uploads folder | Outside `wwwroot`, never served directly |
| Expired shares | Cleanup sweep + expiry check at download time |
| HTTPS | Forced in production |

**Privacy note:** IP grouping means **everyone on the same WiFi can see your shares**. Don't share sensitive data on public WiFi. 6-digit codes are guessable in theory (rate limiting slows this down).

---

## Limits

The 25 MB limit is enforced in **three places**, and all must match:

1. ASP.NET form reading: `FormOptions.MultipartBodyLengthLimit`
2. Kestrel: `MaxRequestBodySize`
3. IIS: `web.config` → `maxAllowedContentLength`

| Limit | Value | Config key |
|---|---|---|
| Max file size | 25 MB | `AirShare:MaxFileSizeBytes` |
| Max active shares per IP | 10 | `AirShare:MaxActiveSharesPerIp` |
| Max text length | 20,000 chars | `AirShare:MaxTextLength` |
| Expiry options | 10/15/30/60 min | `AirShare:AllowedExpiryMinutes` |
| Cleanup interval | 60 s | `AirShare:CleanupIntervalSeconds` |
| Upload rate limit | 10/min | `AirShare:UploadRateLimitPerMinute` |

---

## Deploy to MonsterASP

1. Create a website → choose **"Empty website — no example"**.
2. Create an MSSQL database and run `Database/create_tables.sql` in the SQL editor.
3. **Enable free SSL** (required for IPv4/IPv6 alignment).
4. Copy the **Web Deploy credentials** (port `8172`).
5. Create `appsettings.Production.json` (keep it in `.gitignore`):

```json
{
  "ConnectionStrings": {
    "Default": "Server=xxx.databaseasp.net;Database=xxx;User Id=xxx;Password=***;Encrypt=False;MultipleActiveResultSets=True;TrustServerCertificate=True"
  }
}
```

6. Publish with Web Deploy (Visual Studio → **Publish**, or MSBuild with your publish profile):

```bash
MSBuild.exe share.csproj /t:Publish /p:Configuration=Release /p:PublishProfile=MonsterASP
```

7. Make sure `App_Data/uploads` has **Write** permission.
8. Verify: `https://yoursite.tryasp.net/api/list` should return `{"networkIp":"...","serverTimeUtc":"...","items":[]}`.

> MonsterASP is **x86**, so use a **framework-dependent** publish (`processPath="dotnet"`, `arguments=".\AirShare.dll"`). Don't set `hostingModel` in `web.config`.

---

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `Request entity too large` | Size limits don't match | Align Program.cs (×2) and web.config |
| `File is too large` | App-level limit | Raise `AirShare:MaxFileSizeBytes` |
| `blocked — unsafe file type` | Extension blocklist | Try `.zip` / `.pdf` |
| `Too many requests` | Rate limit | Wait 1 minute |
| 500 with no logs | App didn't start | Check `processPath` in `web.config` |
| Everyone has the same IP | `UseForwardedHeaders` not first | Make it the first middleware |
| Phone ≠ laptop IP | IPv4 vs IPv6 mix | Enable SSL |
| IP shows `unknown` | Proxy sent no header | Contact hosting support |
| `Could not load 'Microsoft.Data.SqlClient'` | `runtimes/` folder not uploaded | Use Web Deploy |
| Cleanup not running | Service not registered | Check logs for `Cleanup: N expired…` |

**Quick IP test:** open `/api/list` on both devices and compare `networkIp`.

---

## License

MIT. Provided as-is, with no warranty. Don't share sensitive data; anyone on the same WiFi can see it.
