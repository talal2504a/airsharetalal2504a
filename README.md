# AirShare

**Live site:** https://yoursite.tryasp.net

Same WiFi par file aur text share karo. **No login, no signup.**

Kisi bhi device se file ya text upload karo → wo turant usi network ke
baaki devices par dikhta hai → expiry ke baad apne aap delete ho jaata hai.

---

## Table of contents

- [Features](#features)
- [Tech stack](#tech-stack)
- [Project structure](#project-structure)
- [How it works](#how-it-works)
- [Setup (local)](#setup-local)
- [API reference](#api-reference)
- [Database](#database)
- [Security](#security)
- [Limits](#limits)
- [Deploy to MonsterASP](#deploy-to-monsterasp)
- [Troubleshooting](#troubleshooting)
- [Build log / lessons](#build-log-lessons)

---

## Features

- **No authentication** — koi account nahi, koi signup nahi
- **IP-based sharing** — same WiFi = same public IP = same list
- **Auto refresh** — list har 3 second poll hoti hai
- **Auto delete** — 10 / 15 / 30 / 60 minute expiry
- **Text sharing** — WiFi password, OTP, notes (20,000 chars tak)
- **Cross-network codes** — 6-digit code se doosre network se bhi download
- **Device name + uploader IP** — list me dikhta hai kaun share kar raha hai
- **Drag & drop** — progress bar ke saath
- **Light / dark theme** — default dark, localStorage me save
- **Responsive** — mobile-first, 44px+ tap targets

---

## Tech stack

| Layer | Technology |
|---|---|
| Backend | ASP.NET Core 8 Web API (C#) |
| ORM | Dapper (fully parameterized SQL) |
| Database | MSSQL (SQL Server 2025) |
| Frontend | Vanilla HTML / CSS / JS — **no framework** |
| Cleanup | `BackgroundService` + `PeriodicTimer` |
| Rate limiting | Built-in `AddRateLimiter` |
| Hosting | MonsterASP.net (IIS, Web Deploy) |

---

## Project structure

```
share/
├── AirShare.csproj                  Project file (net8.0, Dapper, SqlClient)
├── Program.cs                       DI wiring, 3 size limits, rate limiter,
│                                    forwarded headers, static files
├── web.config                       IIS hosting + maxAllowedContentLength
├── appsettings.json                 Config (connection string + limits)
├── appsettings.Development.json     Local dev overrides
├── .gitignore                       Secrets, build output, publish folders
│
├── Controllers/
│   └── ShareController.cs           6 API endpoints + IP access control
│
├── Models/
│   ├── Share.cs                     DB record (Id, NetworkIp, UploaderName, …)
│   └── UploadResult.cs              Upload response DTO
│
├── Data/
│   └── ShareRepository.cs           Dapper queries — all parameterized
│
├── Services/
│   ├── ClientIpService.cs           IP normalize (IPv4 as-is, IPv6 /48)
│   ├── CodeGenerator.cs             Crypto-secure 6-digit code (RNG)
│   ├── FileStorageService.cs        GUID naming, path traversal guard
│   └── CleanupService.cs            BackgroundService — 60s expiry sweep
│
├── wwwroot/
│   ├── index.html                   Single page UI
│   ├── style.css                    Design tokens, light/dark themes
│   ├── app.js                       Polling, upload (XHR), countdown, toasts
│   ├── favicon.svg                  Logo
│   ├── robots.txt                   Search engine rules
│   ├── sitemap.xml                  Sitemap for Google
│   └── google*.html                 Search Console verification file
│
├── App_Data/
│   └── uploads/                     User files — NEVER served directly
│
└── Database/
    └── create_tables.sql            Schema + 3 indexes (idempotent)
```

---

## How it works

```
  📱 Phone ─┐
  💻 Laptop ─┼──► same public IP ──► /api/list ──► same shares
  📱 Tablet ─┘
```

1. Upload → `POST /api/upload`
2. Server real client IP resolve karta hai (`X-Forwarded-For` via `UseForwardedHeaders`)
3. `ClientIpService.Normalize()` IP clean karta hai
4. Row `NetworkIp` ke saath DB me save hoti hai
5. Baaki devices `/api/list` call karte hain → wahi IP match → items dikhte hain
6. Frontend har 3 second poll karta hai → naye items apne aap appear
7. `CleanupService` har 60 second expired rows + files delete karta hai

### IP grouping rules

| Protocol | Rule |
|---|---|
| IPv4 | As-is — `203.0.113.42` |
| IPv4-mapped IPv6 | Unwrap pehle — `::ffff:1.2.3.4` → `1.2.3.4` |
| IPv6 | `/48` prefix — LAN ke saare devices same bucket |

**IPv6 `/48` kyun?** Ghar ka router aksar IPv4 NAT + IPv6 direct dono deta hai.
Phone IPv6 prefer karta hai, laptop IPv4 — dono alag dikhte hain.
`/48` grouping se zyada devices same bucket me aa jaate hain.

⚠️ **HTTPS zaroori hai.** HTTP par kuch networks IPv6 prefer karte hain
jo IPv4/IPv6 mismatch create karta hai. SSL on karne se dono align ho jaate hain.

---

## Setup (local)

### Prerequisites

- .NET 8 SDK (ya .NET 10 SDK — csproj `net8.0` target karta hai)
- SQL Server (LocalDB / Express / full)

### Steps

```bash
# 1. Database banao
sqlcmd -S "(localdb)\MSSQLLocalDB" -Q "CREATE DATABASE AirShare;"

# 2. Tables + indexes
sqlcmd -S "(localdb)\MSSQLLocalDB" -d AirShare -i Database\create_tables.sql

# 3. Connection string — appsettings.json
#    "Server=(localdb)\\MSSQLLocalDB;Database=AirShare;Trusted_Connection=True;TrustServerCertificate=True"

# 4. Run
dotnet restore
dotnet run
```

Browser me `https://localhost:7001` (ya jo bhi port aaye) kholo.

---

## API reference

| Method | Route | Description | Rate limit |
|---|---|---|---|
| POST | `/api/upload` | File ya text upload | 10/min/IP |
| GET | `/api/list` | Caller ke IP ke active shares | — |
| GET | `/api/download/{id}` | File download (same IP) | 60/min/IP |
| GET | `/api/download-by-code/{code}` | Cross-network download | 60/min/IP |
| DELETE | `/api/share/{id}` | Manual delete (same IP) | — |
| GET | `/api/text/{id}` | Text content (same IP) | — |

### Upload request

```
POST /api/upload
Content-Type: multipart/form-data

file          (optional) — the file
text          (optional) — text content
expiryMinutes (optional) — 10 | 15 | 30 | 60
uploaderName  (optional) — display name
```

Ek time pe **file ya text — dono nahi**.

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
| `UploaderName` | `VARCHAR(50)` | Device name ya user ka naam |
| `ShareType` | `VARCHAR(10)` | `CHECK IN ('file','text')` |
| `FileName` | `NVARCHAR(255)` | Original name (display only) |
| `StoredName` | `VARCHAR(100)` | GUID name on disk |
| `TextContent` | `NVARCHAR(MAX)` | Text share ka content |
| `FileSize` | `BIGINT` | Bytes |
| `Code` | `VARCHAR(6)` | `CHECK` digits only, `UNIQUE` filtered index |
| `CreatedAt` | `DATETIME2(3)` | `DEFAULT SYSUTCDATETIME()` |
| `ExpiresAt` | `DATETIME2(3)` | NOT NULL |
| `DownloadCount` | `INT` | `DEFAULT 0` |

### Indexes

| Index | Purpose |
|---|---|
| `IX_Shares_NetworkIp_ExpiresAt` | List query — `WHERE NetworkIp = ? AND ExpiresAt > ?` |
| `IX_Shares_ExpiresAt` | Cleanup query — `WHERE ExpiresAt <= ?` |
| `UX_Shares_Code` | **UNIQUE + FILTERED** — code lookup, NULLs block nahi karte |

> ⚠️ Filtered index ke liye `SET QUOTED_IDENTIFIER ON` aur `SET ANSI_NULLS ON`
> zaroori hai — warna error 1934 aata hai. Script me set hai.

---

## Security

| Layer | Protection |
|---|---|
| SQL injection | Saari queries parameterized (`@param`) — zero concatenation |
| Path traversal | `StoredName` regex + full-path recheck + root prefix verify |
| Filenames on disk | Sirf `Guid.NewGuid()` + sanitized extension |
| Dangerous uploads | 21 extensions blocked (`.exe .bat .cmd .ps1 .js .vbs .msi` …) |
| IP spoofing | Client ka IP kabhi accept nahi hota — resolved address only |
| Cross-network access | `403` jab caller IP share owner se match na kare |
| Brute force | Rate limit 10 uploads/min, 60 downloads/min per IP |
| Codes | `RandomNumberGenerator` (crypto-secure, uniform distribution) |
| XSS | Frontend `textContent` — `innerHTML` sirf static SVG ke liye |
| Downloads | `Content-Disposition: attachment` |
| Uploads folder | `wwwroot` ke bahar — kabhi directly serve nahi hota |
| Expired shares | Cleanup 60s + download-time expiry check |
| HTTPS | Production me forced |

**Privacy note:** IP grouping ka matlab hai **poore WiFi ke logon ko tumhari
files dikhengi**. Public/shared WiFi pe sensitive data mat share karo.
6-digit codes bhi technically guess ho sakte hain (rate limit ke saath slow).

---

## Limits

25 MB size limit **teen jagah** set hai — har layer alag check karti hai:

| # | Layer | Property |
|---|---|---|
| 1 | ASP.NET form reading | `FormOptions.MultipartBodyLengthLimit` |
| 2 | Kestrel | `MaxRequestBodySize` |
| 3 | IIS | `web.config` → `maxAllowedContentLength` |

| Limit | Value | Config key |
|---|---|---|
| Max file size | 25 MB | `AirShare:MaxFileSizeBytes` |
| Max active shares / IP | 10 | `AirShare:MaxActiveSharesPerIp` |
| Max text length | 20,000 chars | `AirShare:MaxTextLength` |
| Expiry options | 10/15/30/60 min | `AirShare:AllowedExpiryMinutes` |
| Cleanup interval | 60 s | `AirShare:CleanupIntervalSeconds` |
| Upload rate limit | 10 /min | `AirShare:UploadRateLimitPerMinute` |

---

## Deploy to MonsterASP

### 1. Panel setup

1. Website banao → **"Empty website — no example"** chuno
2. MSSQL database banao → SQL editor me `Database/create_tables.sql` run karo
3. **Free SSL enable karo** ← zaroori (IPv4/IPv6 alignment ke liye)
4. Database → **Web Deploy credentials** copy karo (port `8172`)

### 2. Connection string

`appsettings.Production.json` banao (ye `.gitignore` me hai — kabhi commit na ho):

```json
{
  "ConnectionStrings": {
    "Default": "Server=xxx.databaseasp.net;Database=xxx;User Id=xxx;Password=***;Encrypt=False;MultipleActiveResultSets=True;TrustServerCertificate=True"
  }
}
```

### 3. Web Deploy

`Properties/PublishProfiles/MonsterASP.pubxml`:

```xml
<Project>
  <PropertyGroup>
    <Configuration>Release</Configuration>
    <Platform>Any CPU</Platform>
    <PublishProtocol>MsDeploy</PublishProtocol>
    <SelfContained>false</SelfContained>
    <WebDeployMethod>MSDeploy</WebDeployMethod>
    <WebSiteName>yoursite</WebSiteName>
    <ServiceURL>https://yoursite.siteasp.net:8172/msdeploy.axd</ServiceURL>
    <MsDeployServiceUrl>https://yoursite.siteasp.net:8172/msdeploy.axd</MsDeployServiceUrl>
    <MsDeploySiteName>yoursite</MsDeploySiteName>
    <DeployIisAppPath>yoursite</DeployIisAppPath>
    <UserName>yoursite</UserName>
    <Password>***</Password>
    <SavePassword>true</SavePassword>
    <DeleteExistingFiles>true</DeleteExistingFiles>
  </PropertyGroup>
</Project>
```

Publish:

```bash
"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" \
  share.csproj /t:Publish /p:Configuration=Release /p:PublishProfile=MonsterASP
```

Ya Visual Studio me: project → **Publish** → profile select → **Publish**.

### 4. Permissions

`App_Data/uploads` — **Write** permission ON (panel → file manager → permissions).
`App_Data` IIS me by default writable hota hai.

### 5. Verify

```
https://yoursite.tryasp.net/api/list
```

Expected: `{"networkIp":"...","serverTimeUtc":"...","items":[]}`

---

## Troubleshooting

### Upload fail

| Symptom | Cause | Fix |
|---|---|---|
| `Request entity too large` | 3 size limits match nahi | Program.cs (×2) + web.config same rakho |
| `File is too large` | App-level limit | `AirShare:MaxFileSizeBytes` badhao |
| `blocked — unsafe file type` | Extension list | `.zip` / `.pdf` try karo |
| `Too many requests` | Rate limit | 1 minute ruko |
| 500 + log nahi | App start nahi hua | `web.config` `processPath` check karo |
| Progress 0 pe atak gaya | Proxy timeout | Hosting support se baat karo |

### IP galat dikha raha hai

| Symptom | Cause | Fix |
|---|---|---|
| Sab ka same IP | `UseForwardedHeaders` first middleware nahi | Program.cs me sabse upar hona chahiye |
| Phone ≠ laptop | **IPv4 vs IPv6 mix** | **SSL enable karo** ← yahi main reason tha |
| IP `unknown` | Proxy ne header nahi bheja | Hosting support |
| Har device alag | IPv6 privacy addresses | `/48` grouping (already implemented) |

**Test:** `/api/list` kholo — JSON me `networkIp` dikhega. Dono devices pe compare karo.

### Assembly load errors

```
FileNotFoundException: Could not load 'Microsoft.Data.SqlClient'
```

**Cause:** publish output ka `runtimes/` folder upload nahi hua.

**Fix:** Web Deploy use karo (folder structure preserve karta hai), ya manually
`runtimes\win-x86\native\Microsoft.Data.SqlClient.SNI.dll` upload karo.

### Cleanup nahi ho raha

App logs me dekho:

```
Cleanup: 5 expired me se 5 delete, 0 retry pending.
```

- `retry pending > 0` → file lock hai, agli cycle me retry hoga
- Log hi nahi → `CleanupService` register nahi hua

### 500 with empty body

- `hostingModel` attribute web.config me mat rakho (.NET Core 3+ invalid)
- Self-contained + `hostingModel="inprocess"` = conflict
- Framework-dependent: `processPath="dotnet"` + `arguments=".\AirShare.dll"`

---

## Build log / lessons

Is project me jo issues aaye aur solve hue:

1. **`InvariantGlobalization=true`** → SqlClient ko locale data chahiye,
   "Globalization Invariant Mode is not supported" exception. Hat diya.

2. **`IDbConnection` missing** → `using System.Data;` add kiya.

3. **`File.Exists()` vs `ControllerBase.File()`** → conflict. `System.IO.File.Exists()`
   explicitly use karna pada.

4. **`hostingModel` attribute** → .NET 6+ me invalid, config reject hota tha.

5. **`X-Forwarded-For` missing `runtimes/` folder** → FTP se folder structure
   nahi gaya, `Microsoft.Data.SqlClient.SNI.dll` missing raha. **Web Deploy ne solve kiya.**

6. **Self-contained + x64** → MonsterASP sirf **x86** hai. Bitness mismatch
   ("HTTP Error 500.32 - Failed to load .NET Core host").
   Solution: framework-dependent publish.

7. **IPv4 vs IPv6** → phone IPv6, laptop IPv4 → alag IP → sharing fail.
   **SSL enable karne se fix hua.** Ye biggest find tha.

8. **CSS class mismatch** → `app.js` `btn-accent` use karta tha, CSS me `btn-ok`
   tha → buttons invisible. Aliases add kiye.

9. **`XHR responseText` throws** jab `responseType='json'` set ho.
   Guard add kiya.

---

## License

MIT — koi bhi use kar sakta hai.

**Disclaimer:** Provided as-is, no warranty. Sensitive data share mat karo —
WiFi par koi bhi ye dekh sakta hai.
