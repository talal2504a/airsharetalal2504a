using System.Text;
using AirShare.Data;
using AirShare.Models;
using AirShare.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AirShare.Controllers;

/// <summary>
/// Saari share API endpoints.
///
/// SECURITY: har request pe caller's IP verify hota hai — doosre network
/// ko kuch nahi milega. Sirf /download-by-code IP check NAHI karta (by design).
/// </summary>
[ApiController]
[Route("api")]
[Produces("application/json")]
public class ShareController : ControllerBase
{
    private readonly ShareRepository _repo;
    private readonly IFileStorageService _storage;
    private readonly IClientIpService _ipService;
    private readonly IConfiguration _config;
    private readonly ILogger<ShareController> _logger;

    public ShareController(
        ShareRepository repo,
        IFileStorageService storage,
        IClientIpService ipService,
        IConfiguration config,
        ILogger<ShareController> logger)
    {
        _repo = repo;
        _storage = storage;
        _ipService = ipService;
        _config = config;
        _logger = logger;
    }

    // ================================================================
    // POST /api/upload
    // ================================================================

    /// <summary>
    /// File ya text upload karo. Ek time pe sirf EK — dono nahi.
    /// FormData fields: file (optional), text (optional), expiryMinutes (optional).
    /// </summary>
    [HttpPost("upload")]
    [EnableRateLimiting("uploads")]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<UploadResult>> Upload(
        [FromForm(Name = "file")] IFormFile? file,
        [FromForm(Name = "text")] string? text,
        [FromForm(Name = "expiryMinutes")] int? expiryMinutes)
    {
        var networkIp = _ipService.GetNetworkId(HttpContext);
        var now = DateTime.UtcNow;

        var hasFile = file is not null && file.Length > 0;
        var hasText = !string.IsNullOrWhiteSpace(text);

        if (hasFile && hasText)
        {
            return BadRequest(new UploadResult
            {
                Success = false,
                Message = "Ek time pe sirf file ya sirf text bhejo."
            });
        }

        if (!hasFile && !hasText)
        {
            return BadRequest(new UploadResult
            {
                Success = false,
                Message = "Kuch share karna hai — file select karo ya text likho."
            });
        }

        // ---- Expiry validate karo ----
        var allowedExpiries = _config.GetSection("AirShare:AllowedExpiryMinutes").Get<int[]>()
            ?? new[] { 10, 15, 30, 60 };

        var defaultExpiry = _config.GetValue("AirShare:DefaultExpiryMinutes", 15);
        var expiry = expiryMinutes ?? defaultExpiry;

        if (!allowedExpiries.Contains(expiry))
        {
            return BadRequest(new UploadResult
            {
                Success = false,
                Message = "Expiry time galat hai. Valid: "
                          + string.Join(", ", allowedExpiries) + " minutes."
            });
        }

        // ---- Per-IP active share limit (max 10) ----
        var maxActive = _config.GetValue("AirShare:MaxActiveSharesPerIp", 10);
        var activeCount = await _repo.CountActiveByNetworkAsync(networkIp, now);

        if (activeCount >= maxActive)
        {
            return BadRequest(new UploadResult
            {
                Success = false,
                Message = $"Is network pe already {maxActive} active items hain. "
                          + "Thodi der baad try karo."
            });
        }

        // ---- Share object banao ----
        var share = new Share
        {
            NetworkIp = networkIp,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(expiry),
            ShareType = hasFile ? "file" : "text",
            DownloadCount = 0
        };

        // ---- File handle ----
        if (hasFile)
        {
            try
            {
                var stored = await _storage.SaveAsync(file!);
                share.StoredName = stored.StoredName;
                share.FileName = stored.OriginalName;
                share.FileSize = stored.Size;
            }
            catch (InvalidOperationException ex)
            {
                // Size ya extension validation fail — user ko message dikhao
                return BadRequest(new UploadResult
                {
                    Success = false,
                    Message = ex.Message
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "File save fail. Network: {Ip}", networkIp);
                return StatusCode(StatusCodes.Status500InternalServerError,
                    new UploadResult
                    {
                        Success = false,
                        Message = "File save nahi ho payi. Baad me try karo."
                    });
            }
        }
        else
        {
            // ---- Text handle ----
            var maxText = _config.GetValue("AirShare:MaxTextLength", 20000);
            var content = text!.Trim();

            if (content.Length > maxText)
            {
                return BadRequest(new UploadResult
                {
                    Success = false,
                    Message = $"Text bahut lamba hai. Max {maxText:N0} characters."
                });
            }

            share.TextContent = content;
            share.FileName = BuildTextFileName(content);
            share.FileSize = Encoding.UTF8.GetByteCount(content);
        }

        // ---- Unique code generate karo ----
        var code = await GenerateUniqueCodeAsync();
        share.Code = code;

        // ---- DB me insert karo ----
        try
        {
            share.Id = await _repo.InsertAsync(share);
        }
        catch (Exception ex)
        {
            // Insert fail hua to orphan file mat chhodo — delete kar do
            if (hasFile && share.StoredName is not null)
            {
                _storage.DeleteQuietly(share.StoredName);
            }

            _logger.LogError(ex, "DB insert fail. Network: {Ip}", networkIp);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new UploadResult { Success = false, Message = "Share save nahi ho payi." });
        }

        return StatusCode(StatusCodes.Status201Created, new UploadResult
        {
            Success = true,
            Message = hasFile ? "File share ho gaya." : "Text share ho gaya.",
            Id = share.Id,
            Code = share.Code,
            ExpiresAt = share.ExpiresAt
        });
    }

    /// <summary>
    /// Code generate karta hai aur DB se verify karta hai ki unique hai.
    /// 6 digits = 1M combinations, isliye collision retries zaroori hain.
    /// </summary>
    private async Task<string> GenerateUniqueCodeAsync()
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var candidate = CodeGenerator.Generate();
            var existing = await _repo.GetByCodeAsync(candidate);

            if (existing is null)
            {
                return candidate;
            }

            _logger.LogInformation("Code {Code} collision, retry {Attempt}",
                candidate, attempt + 1);
        }

        throw new InvalidOperationException("Unique code generate nahi ho paya.");
    }

    // ================================================================
    // GET /api/list
    // ================================================================

    /// <summary>
    /// Caller ke network ke active shares. Doosre network ko empty list milega.
    /// </summary>
    [HttpGet("list")]
    public async Task<ActionResult<ShareListResponse>> List()
    {
        var networkIp = _ipService.GetNetworkId(HttpContext);
        var now = DateTime.UtcNow;

        var shares = await _repo.GetActiveByNetworkAsync(networkIp, now);
        const int previewLength = 120;

        var items = shares.Select(s => new ShareListItem
        {
            Id = s.Id,
            Type = s.ShareType,
            FileName = s.FileName,
            FileSize = s.FileSize,

            // Text ka poora content list me NAHI bhejte — bandwidth bachane ke liye
            // sirf preview. Full text /api/text/{id} se aayega.
            TextPreview = s.IsText
                ? (s.TextContent!.Length <= previewLength
                    ? s.TextContent
                    : s.TextContent[..previewLength] + "...")
                : null,

            Code = s.Code,
            CreatedAtUtc = DateTime.SpecifyKind(s.CreatedAt, DateTimeKind.Utc),
            ExpiresAtUtc = DateTime.SpecifyKind(s.ExpiresAt, DateTimeKind.Utc),
            DownloadCount = s.DownloadCount
        }).ToList();

        return Ok(new ShareListResponse
        {
            NetworkIp = networkIp,

            // Server time bhejte hain — frontend clock compare karke
            // countdown accurate rakhta hai
            ServerTimeUtc = now,
            Items = items
        });
    }

    // ================================================================
    // GET /api/download/{id}
    // ================================================================

    /// <summary>File download — sirf wahi IP jisne upload kiya.</summary>
    [HttpGet("download/{id:int}")]
    [EnableRateLimiting("downloads")]
    public async Task<IActionResult> Download(int id)
    {
        var share = await _repo.GetByIdAsync(id);

        if (share is null)
        {
            return NotFound(new
            {
                success = false,
                message = "Ye item nahi mila (expired ho gaya?)."
            });
        }

        // ---- CRITICAL: cross-network access block ----
        var networkIp = _ipService.GetNetworkId(HttpContext);

        if (!string.Equals(share.NetworkIp, networkIp, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Blocked cross-network download. Share #{Id}", id);
            return StatusCode(StatusCodes.Status403Forbidden,
                new { success = false, message = "Ye item tumhare network ka nahi hai." });
        }

        if (!share.IsFile)
        {
            return BadRequest(new
            {
                success = false,
                message = "Ye text share hai — Copy button use karo."
            });
        }

        if (!_storage.TryGetPath(share.StoredName ?? string.Empty, out var path)
        || !System.IO.File.Exists(path))
        {
            return NotFound(new
            {
                success = false,
                message = "File disk pe nahi mili."
            });
        }

        await _repo.IncrementDownloadAsync(id);

        // File() method automatically Content-Disposition: attachment set karta
        // hai + ASCII fallback filename bhi deta hai (purane browsers ke liye)
        var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, useAsync: true);

        return File(stream, "application/octet-stream", share.FileName,
            enableRangeProcessing: true);
    }

    // ================================================================
    // GET /api/download-by-code/{code}
    // ================================================================

    /// <summary>
    /// Cross-network download — user ke 6-digit code se.
    /// Ye INTENTIONALLY IP check NAHI karta — isliye code ko secret maano.
    /// </summary>
    [HttpGet("download-by-code/{code}")]
    [EnableRateLimiting("downloads")]
    public async Task<IActionResult> DownloadByCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length != 6 || !code.All(char.IsDigit))
        {
            return BadRequest(new
            {
                success = false,
                message = "Code 6 digits ka hona chahiye."
            });
        }

        var share = await _repo.GetByCodeAsync(code);

        if (share is null)
        {
            return NotFound(new
            {
                success = false,
                message = "Ye code galat ya expired hai."
            });
        }

        if (share.ExpiresAt <= DateTime.UtcNow)
        {
            return NotFound(new
            {
                success = false,
                message = "Ye code expire ho gaya."
            });
        }

        // Text share — JSON me content bhej do        if (!share.IsFile)
        {
            return Ok(new
            {
                success = true,
                type = "text",
                fileName = share.FileName,
                text = share.TextContent
            });
        }

        if (!_storage.TryGetPath(share.StoredName ?? string.Empty, out var path)
           || !System.IO.File.Exists(path))
        {
            return NotFound(new
            {
                success = false,
                message = "File disk pe nahi mili."
            });
        }

        await _repo.IncrementDownloadAsync(share.Id);

        var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, useAsync: true);

        return File(stream, "application/octet-stream", share.FileName,
            enableRangeProcessing: true);
    }

    // ================================================================
    // DELETE /api/share/{id}
    // ================================================================

    /// <summary>Manual delete — sirf wahi IP jo owner hai.</summary>
    [HttpDelete("share/{id:int}")]
    public async Task<ActionResult> Delete(int id)
    {
        var networkIp = _ipService.GetNetworkId(HttpContext);
        var share = await _repo.GetByIdAsync(id);

        if (share is null)
        {
            return NotFound(new { success = false, message = "Item nahi mila." });
        }

        if (!string.Equals(share.NetworkIp, networkIp, StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                new { success = false, message = "Ye item tumhare network ka nahi hai." });
        }

        // Pehle file hatao, phir row — taaki orphan file na rahe
        if (share.IsFile && share.StoredName is not null)
        {
            _storage.DeleteQuietly(share.StoredName);
        }

        await _repo.DeleteByIdAsync(id);

        return Ok(new { success = true, message = "Delete ho gaya." });
    }

    // ================================================================
    // GET /api/text/{id}
    // ================================================================

    /// <summary>Text share ka full content — sirf same IP.</summary>
    [HttpGet("text/{id:int}")]
    public async Task<ActionResult> GetText(int id)
    {
        var share = await _repo.GetByIdAsync(id);

        if (share is null)
        {
            return NotFound(new { success = false, message = "Item nahi mila." });
        }

        var networkIp = _ipService.GetNetworkId(HttpContext);

        if (!string.Equals(share.NetworkIp, networkIp, StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                new { success = false, message = "Ye item tumhare network ka nahi hai." });
        }

        if (!share.IsText)
        {
            return BadRequest(new { success = false, message = "Ye file share hai." });
        }

        return Ok(new { success = true, id = share.Id, text = share.TextContent });
    }

    // ================================================================
    // HELPERS
    // ================================================================

    /// <summary>
    /// Text share ka suggested filename. First line se banata hai.
    /// Jaise "WiFi Password: 12345" -> "wifi-password-12345.txt"
    /// </summary>
    private static string BuildTextFileName(string content)
    {
        var firstLine = content
            .Split('\n', StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "text";

        var preview = firstLine.Length > 30 ? firstLine[..30] : firstLine;

        // Filename me sirf safe characters rakho
        var safe = new string(preview
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray());

        safe = safe.Trim('-');
        if (safe.Length == 0) safe = "note";

        return safe + ".txt";
    }

    // ================================================================
    // DTOs
    // File list exact rakhne ke liye yahin define hain (alag file nahi banayi)
    // ================================================================

    public sealed class ShareListResponse
    {
        /// <summary>Caller ka network id — frontend header me dikhata hai.</summary>
        public string NetworkIp { get; init; } = string.Empty;

        /// <summary>Server ka UTC time — countdown sync ke liye.</summary>
        public DateTime ServerTimeUtc { get; init; }

        public IReadOnlyList<ShareListItem> Items { get; init; }
            = Array.Empty<ShareListItem>();
    }

    public sealed class ShareListItem
    {
        public int Id { get; init; }

        /// <summary>"file" ya "text".</summary>
        public string Type { get; init; } = string.Empty;

        public string? FileName { get; init; }
        public long? FileSize { get; init; }

        /// <summary>Text ka chhota preview (list me dikhane ke liye).</summary>
        public string? TextPreview { get; init; }

        /// <summary>6-digit code.</summary>
        public string? Code { get; init; }

        public DateTime CreatedAtUtc { get; init; }
        public DateTime ExpiresAtUtc { get; init; }
        public int DownloadCount { get; init; }
    }
}