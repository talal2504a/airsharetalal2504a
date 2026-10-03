# AirShare

Same WiFi pe file aur text share karo. No login, no signup.

Koi file ya text upload karo → wo tumhare **public IP** se group hota hai →
us hi network ke kisi bhi device pe website kholte hi item dikh jaata hai →
expiry ke baad apne aap delete ho jaata hai.

---

## Features

- **No login / no signup** — koi account nahi, koi cookie nahi
- **IP-based sharing** — same WiFi = same public IP = same list
- **Auto-refresh** — list har 3 second me poll hoti hai
- **Auto-delete** — 10min / 15min / 30min / 1 hour expiry
- **Text sharing** — WiFi password, OTP, notes (20,000 chars tak)
- **Cross-network codes** — 6-digit code se doosre network pe bhi download
- **Drag & drop** — mobile-friendly, progress bar ke saath
- **Light / dark theme** — default dark

---

## Project structure

```
share/
├── AirShare.csproj / share.csproj
├── Program.cs                 DI,3 size limits, rate limiter, forwarded headers
├── web.config                 IIS hosting + maxAllowedContentLength
├── appsettings.json           Config (connection string + limits)
├── Controllers/
│   └── ShareController.cs     6 API endpoints
├── Models/
│   ├── Share.cs               DB record
│   └── UploadResult.cs        Upload response
├── Data/
│   └── ShareRepository.cs     Dapper, parameterized SQL
├── Services/
│   ├── ClientIpService.cs     IP normalization (IPv4 / IPv6 /64)
│   ├── CodeGenerator.cs       Crypto-secure 6-digit codes
│   ├── FileStorageService.cs  GUID naming + path traversal protection
│   └── CleanupService.cs      BackgroundService (60s expiry cleanup)
├── wwwroot/
│   ├── index.html
│   ├── style.css
│   ├── app.js
│   └── favicon.svg
├── App_Data/uploads/          User files (NEVER served directly)
├── Database/
│   └── create_tables.sql
└── README.md
```

---

## 1. Local setup

### Prerequisites
- .NET 8 SDK (ya .NET 10 SDK — csproj me `net8.0` rakho)
- SQL Server (local ya remote)

### Steps

**a) Database banao**

SQL Server me `AirShare` database bana lo, phir
`Database/create_tables.sql` ko SSMS me pura run karo.

Ya directly:

```sql
CREATE DATABASE AirShare;
GO
-- phir create_tables.sql ka content run karo
```

**b) Connection string**

`appsettings.json` me `ConnectionStrings:Default` set karo:

```json
"Default": "Server=localhost;Database=AirShare;Trusted_Connection=True;TrustServerCertificate=True"
```

Ya SQL auth se:

```json
"Default": "Server=localhost;Database=AirShare;User Id=sa;Password=YOUR_PASSWORD;TrustServerCertificate=True"
```

**c) Run**

```bash
dotnet restore
dotnet run
```

Browser me `https://localhost:7001` (ya jo bhi port aaye) kholo.

---

## 2. API endpoints

| Method | Route | Description |
|---|---|---|
| POST | `/api/upload` | File ya text upload (multipart). Rate limited10/min/IP |
| GET | `/api/list` | Caller ke IP ke active shares + server time |
| GET | `/api/download/{id}` | File download (same IP only) |
| GET | `/api/download-by-code/{code}` | Cross-network download by6-digit code |
| DELETE | `/api/share/{id}` | Manual delete (same IP only) |
| GET | `/api/text/{id}` | Text content (same IP only) |

**Upload request format:**

```
POST /api/upload
Content-Type: multipart/form-data

file          (optional) — the file
text          (optional) — text content
expiryMinutes (optional) — 10 | 15 | 30 | 60
```

Ek time pe **file ya text, dono nahi**.

---

## 3. How IP grouping works

Ye app ka core concept hai:

1. Hosting ke aage proxy hai → `UseForwardedHeaders` (`X-Forwarded-For`) se real client IP milta hai
2. `ClientIpService.Normalize()` IP ko clean karta hai:
   - **IPv4** → as-is (`203.0.113.42`)
   - **IPv6** → pehle `/64` bits (har ghar ko ek /64 block milta hai, isliye same wifi ke devices same prefix share karte hain)
3. Shares `NetworkIp` column se group hote hain
4. Har request pe caller ka IP us share ke IP se match hona chahiye — warna **403**

⚠️ **Important:** IP grouping ka matlab hai ki **poore WiFi ke logon ko tumhari files dikhengi**. Public/shared WiFi pe sensitive data mat share karo.

---

## 4. Security

| Layer | Kya kiya |
|---|---|
| File naming | Disk pe `Guid.NewGuid()` + sanitized extension — user ka filename kabhi use nahi hota |
| Path traversal | `StoredName` regex validation + full-path recheck |
| Dangerous extensions | `.exe .bat .cmd .ps1 .js .vbs .msi .scr...` blocked |
| SQL injection | Saari queries parameterized (Dapper `@param`) |
| IP spoofing | Client ka IP kabhi accept nahi hota — sirf resolved remote address |
| Rate limiting | 10 uploads/min/IP, 60 downloads/min/IP |
| XSS | Frontend `textContent` use karta hai, `innerHTML` sirf static SVG ke liye |
| Downloads | `Content-Disposition: attachment` |
| Uploads folder | `wwwroot` ke bahar — kabhi directly serve nahi hota |
| HTTPS | Production me force |
| Codes | `RandomNumberGenerator` se6-digit |

---

## 5. Limits

3 jagah same 25 MB limit (har layer pe alag check zaroori hai):

1. `Program.cs` → `FormOptions.MultipartBodyLengthLimit`
2. `Program.cs` → Kestrel `MaxRequestBodySize`
3. `web.config` → `maxAllowedContentLength`

Baaki limits:

| Limit | Value | Config key |
|---|---|---|
| Max file size | 25 MB | `AirShare:MaxFileSizeBytes` |
| Max active shares / IP | 10 | `AirShare:MaxActiveSharesPerIp` |
| Max text length | 20,000 chars | `AirShare:MaxTextLength` |
| Expiry options | 10/15/30/60 min | `AirShare:AllowedExpiryMinutes` |
| Cleanup interval | 60s | `AirShare:CleanupIntervalSeconds` |

---

## 6. Deploy to MonsterASP.net

### a) Panel setup

1. MonsterASP panel me **naya website** banao
2. **MSSQL database** banao — panel connection string copy kar lo
3. Panel ke **SQL editor** me `Database/create_tables.sql` ka content paste karke run karo
4. Panel se **free SSL** enable karo

### b) Connection string (secret!)

`appsettings.Production.json` banao:

```json
{
  "ConnectionStrings": {
    "Default": "<MONSTERASP-PANEL-WALI-CONNECTION-STRING>"
  }
}
```

⚠️ Ye file `.gitignore` me hai — kabhi commit mat karna.

### c) Publish

**Web Deploy (Visual Studio):**

1. Solution Explorer me project pe right-click → **Publish**
2. Target: **Folder** → `bin\Release\net8.0\publish`
3. Publish profile save karo (`Properties\PublishProfiles\`)
4. Panel me login karke **Web Deploy** credentials lo
5. VS me publish profile select karke **Publish** dabao

Ya CLI se:

```bash
dotnet publish -c Release -o ./publish
```

phir panel ke **File Manager / FTP** se `publish` folder ka content upload karo.

### d) Permissions

`App_Data/uploads` folder pe **write permission** chahiye. Panel me folder pe right-click → **Permissions** → `Write` ON.

Agar500 error aaye, to ye check karo:
- `App_Data/uploads` exist karta hai?
- Write permission hai?

### e) Verify

Domain kholo aur test karo:

- [ ] Phone pe file upload
- [ ] Laptop pe (same WiFi) list me dikhta hai?
- [ ] Download kaam karta hai?
- [ ] Text share + Copy button?
- [ ] Expiry ke baad auto-delete?
- [ ] **Doosre network (mobile data) pe same URL kholo → list KHALI honi chahiye**
- [ ] 6-digit code se cross-network download?
- [ ] `App_Data/uploads/kuch.pdf` URL pe 404 de raha hai?

---

## 7. Troubleshooting

### Upload fail ho raha hai

| Problem | Fix |
|---|---|
| 400 "Request entity too large" | 3 limits match karo (Program.cs ×2, web.config) |
| 400 "File bahut badi hai" | `MaxFileSizeBytes` badhao (teeno jagah) |
| 400 "unsafe file type" | Extension blocked hai — `.zip`/`.pdf` try karo |
| 429 "Bahut zyada requests" | Rate limit — 1 minute ruko |
| 500 + "Uploads folder ready" log nahi | `App_Data/uploads` permission check karo |
| Progress bar 0 pe atak gaya | Reverse proxy ka request timeout — usually hosting side |

### IP galat dikha raha hai

| Problem | Fix |
|---|---|
| Sab users ka same IP | `UseForwardedHeaders` `Program.cs` me **pehle** middleware hona chahiye |
| IP `unknown` | Proxy ne `X-Forwarded-For` nahi bheja — hosting support se baat karo |
| Har device ka alag IP | IPv6 me `/64` grouping check karo — ya carrier-grade NAT (mobile data) hai |

**Test:** Browser me `https://yourdomain.com/api/list` kholo — JSON me `networkIp` dikhega.

### Permission errors

```
Access to the path '...\App_Data\uploads\xxx.tmp' is denied
```

- Folder exist karta hai? → `mkdir App_Data/uploads`
- Write permission? → Panel me folder permissions me `Write` ON
- App pool identity? → App_Data IIS me default writeable hota hai
- `TrustServerCertificate=True` missing? → SSL cert verify nahi ho raha

### Cleanup nahi ho raha

App ke logs me dekho:

```
Cleanup: 5 expired me se 5 delete, 0 retry pending.
```

- `retry pending` > 0 → file lock hai (koi download kar raha hai) — agli cycle me retry hoga
- Koi log hi nahi → `CleanupService` register nahi hua (Program.cs check karo)

### List khali hai doosre device pe

1. Dono devices **same WiFi** pe hain? (mobile data pe nahi)
2. Same **public IP**? → `/api/list` dono se check karo
3. App **same URL** pe chal raha hai?
4. Share expire to nahi ho gaya?

---

## 8. Security notes

- **No authentication by design** — IP grouping ka matlab hai WiFi ke kisi bhi member ko files mil sakti hain
- **Codes guess ho sakte hain** (6 digits = 1M combos) — rate limiter 60/min pe hai, to brute force slow hai par impossible nahi
- **Public WiFi pe sensitive data mat share karo**
- SSL zaroor enable karo (clipboard API plain HTTP pe kaam nahi karta — fallback hai, par HTTPS better hai)

---

## 9. Tech stack

- **Backend:** ASP.NET Core 8 Web API, Dapper, Microsoft.Data.SqlClient
- **Frontend:** Vanilla HTML/CSS/JS (no framework), 3s polling
- **DB:** MSSQL (SQL Server)
- **Hosting:** MonsterASP.net (IIS, Web Deploy / FTP)
- **Cleanup:** `BackgroundService` + `PeriodicTimer`