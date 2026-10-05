using System.Text;
using AirShare.Data;
using AirShare.Models;
using AirShare.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AirShare.Controllers;

/// <summary>
/// All share API endpoints.
///
/// SECURITY: every request validates the caller's IP — other networks get
/// nothing. Only /download-by-code skips the IP check (by design).
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
    /// Upload a file OR text — never both at once.
    /// FormData fields: file (optional), text (optional),
    ///                 expiryMinutes (optional), uploaderName (optional).
    /// </summary>
    [HttpPost("upload")]
    [EnableRateLimiting("uploads")]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<UploadResult>> Upload(
        [FromForm(Name = "file")] IFormFile? file,
        [FromForm(Name = "text")] string? text,
        [FromForm(Name = "expiryMinutes")] int? expiryMinutes,
        [FromForm(Name = "uploaderName")] string? uploaderName)
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
                Message = "Send either a file or text, not both."
            });
        }

        if (!hasFile && !hasText)
        {
            return BadRequest(new UploadResult
            {
                Success = false,
                Message = "Nothing to share — select a file or type some text."
            });
        }

        // ---- Validate expiry ----
        var allowedExpiries = _config.GetSection("AirShare:AllowedExpiryMinutes").Get<int[]>()
            ?? new[] { 10, 15, 30, 60 };

        var defaultExpiry = _config.GetValue("AirShare:DefaultExpiryMinutes", 15);
        var expiry = expiryMinutes ?? defaultExpiry;

        if (!allowedExpiries.Contains(expiry))
        {
            return BadRequest(new UploadResult
            {
                Success = false,
                Message = "Invalid expiry. Valid values: "
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
                Message = $"This network already has {maxActive} active items. "
                          + "Try again later."
            });
        }

        // ---- Build share object ----
        var share = new Share
        {
            NetworkIp = networkIp,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(expiry),
            ShareType = hasFile ? "file" : "text",
            UploaderName = SanitizeName(uploaderName),
            DownloadCount = 0
        };

        // ---- Handle file ----
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
                // Size or extension validation failed — show message to user
                return BadRequest(new UploadResult
                {
                    Success = false,
                    Message = ex.Message
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "File save failed. Network: {Ip}", networkIp);
                return StatusCode(StatusCodes.Status500InternalServerError,
                    new UploadResult
                    {
                        Success = false,
                        Message = "Could not save the file. Please try again."
                    });
            }
        }
        else
        {
            // ---- Handle text ----
            var maxText = _config.GetValue("AirShare:MaxTextLength", 20000);
            var content = text!.Trim();

            if (content.Length > maxText)
            {
                return BadRequest(new UploadResult
                {
                    Success = false,
                    Message = $"Text is too long. Maximum is {maxText:N0} characters."
                });
            }

            share.TextContent = content;
            share.FileName = BuildTextFileName(content);
            share.FileSize = Encoding.UTF8.GetByteCount(content);
        }

        // ---- Generate unique code ----
        var code = await GenerateUniqueCodeAsync();
        share.Code = code;

        // ---- Insert into DB ----
        try
        {
            share.Id = await _repo.InsertAsync(share);
        }
        catch (Exception ex)
        {
            // Insert failed — delete the orphaned file so it does not linger
            if (hasFile && share.StoredName is not null)
            {
                _storage.DeleteQuietly(share.StoredName);
            }

            _logger.LogError(ex, "DB insert failed. Network: {Ip}", networkIp);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new UploadResult { Success = false, Message = "Could not save the share." });
        }

        return StatusCode(StatusCodes.Status201Created, new UploadResult
        {
            Success = true,
            Message = hasFile ? "File shared successfully." : "Text shared successfully.",
            Id = share.Id,
            Code = share.Code,
            ExpiresAt = share.ExpiresAt
        });
    }

    /// <summary>
    /// Generates a code and verifies uniqueness in the DB.
    /// 6 digits = 1M combinations, so collision retries are necessary.
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

        throw new InvalidOperationException("Could not generate a unique code.");
    }

    // ================================================================
    // GET /api/list
    // ================================================================

    /// <summary>
    /// Active shares for the caller's network. Other networks get an empty list.
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

            // Full text content is NOT sent in the list — only a short preview,
            // to save bandwidth. Full text comes from /api/text/{id}.
            TextPreview = s.IsText
                ? (s.TextContent!.Length <= previewLength
                    ? s.TextContent
                    : s.TextContent[..previewLength] + "...")
                : null,

            Code = s.Code,
            UploaderName = s.UploaderName,
            NetworkIp = s.NetworkIp,
            CreatedAtUtc = DateTime.SpecifyKind(s.CreatedAt, DateTimeKind.Utc),
            ExpiresAtUtc = DateTime.SpecifyKind(s.ExpiresAt, DateTimeKind.Utc),
            DownloadCount = s.DownloadCount
        }).ToList();

        return Ok(new ShareListResponse
        {
            NetworkIp = networkIp,

            // Server time lets the frontend sync its countdown clock
            ServerTimeUtc = now,
            Items = items
        });
    }

    // ================================================================
    // GET /api/download/{id}
    // ================================================================

    /// <summary>Download a file — only the uploading IP may do this.</summary>
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
                message = "Item not found — it may have expired."
            });
        }

        // ---- CRITICAL: block cross-network access ----
        var networkIp = _ipService.GetNetworkId(HttpContext);

        if (!string.Equals(share.NetworkIp, networkIp, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Blocked cross-network download. Share #{Id}", id);
            return StatusCode(StatusCodes.Status403Forbidden,
                new { success = false, message = "This item is not on your network." });
        }

        if (!share.IsFile)
        {
            return BadRequest(new
            {
                success = false,
                message = "This is a text share — use the Copy button instead."
            });
        }

        if (!_storage.TryGetPath(share.StoredName ?? string.Empty, out var path)
            || !System.IO.File.Exists(path))
        {
            return NotFound(new
            {
                success = false,
                message = "File not found on disk."
            });
        }

        await _repo.IncrementDownloadAsync(id);

        // File() automatically sets Content-Disposition: attachment and
        // provides an ASCII fallback filename for older browsers.
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
    /// Cross-network download via the 6-digit code.
    /// This intentionally does NOT check IP — treat the code as a secret.
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
                message = "Code must be exactly 6 digits."
            });
        }

        var share = await _repo.GetByCodeAsync(code);

        if (share is null)
        {
            return NotFound(new
            {
                success = false,
                message = "Invalid or expired code."
            });
        }

        if (share.ExpiresAt <= DateTime.UtcNow)
        {
            return NotFound(new
            {
                success = false,
                message = "This code has expired."
            });
        }

        // Text share — return content as JSON
        if (!share.IsFile)
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
                message = "File not found on disk."
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

    /// <summary>Manual delete — only the owning IP may delete.</summary>
    [HttpDelete("share/{id:int}")]
    public async Task<ActionResult> Delete(int id)
    {
        var networkIp = _ipService.GetNetworkId(HttpContext);
        var share = await _repo.GetByIdAsync(id);

        if (share is null)
        {
            return NotFound(new { success = false, message = "Item not found." });
        }

        if (!string.Equals(share.NetworkIp, networkIp, StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                new { success = false, message = "This item is not on your network." });
        }

        // Delete the file first, then the row — avoids orphaned files
        if (share.IsFile && share.StoredName is not null)
        {
            _storage.DeleteQuietly(share.StoredName);
        }

        await _repo.DeleteByIdAsync(id);

        return Ok(new { success = true, message = "Deleted." });
    }

    // ================================================================
    // GET /api/text/{id}
    // ================================================================

    /// <summary>Full text content of a share — same IP only.</summary>
    [HttpGet("text/{id:int}")]
    public async Task<ActionResult> GetText(int id)
    {
        var share = await _repo.GetByIdAsync(id);

        if (share is null)
        {
            return NotFound(new { success = false, message = "Item not found." });
        }

        var networkIp = _ipService.GetNetworkId(HttpContext);

        if (!string.Equals(share.NetworkIp, networkIp, StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                new { success = false, message = "This item is not on your network." });
        }

        if (!share.IsText)
        {
            return BadRequest(new { success = false, message = "This is a file share." });
        }

        return Ok(new { success = true, id = share.Id, text = share.TextContent });
    }

    // ================================================================
    // HELPERS
    // ================================================================

    /// <summary>
    /// Sanitizes the uploader's name.
    /// DB column is VARCHAR(50) — strips control and path characters.
    /// Falls back to "Anonymous" when the name is empty.
    /// </summary>
    private static string SanitizeName(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "Anonymous";

        var sb = new StringBuilder(input.Length);
        foreach (var ch in input.Trim())
        {
            if (char.IsControl(ch)) continue;
            if (ch is '"' or '\'' or '\\' or '/' or '<') continue;
            sb.Append(ch);
        }

        var clean = sb.ToString().Trim();
        if (clean.Length == 0) return "Anonymous";
        return clean.Length > 50 ? clean[..50] : clean;
    }

    /// <summary>
    /// Builds a suggested filename for text shares from the first line.
    /// "WiFi Password: 12345" becomes "wifi-password-12345.txt"
    /// </summary>
    private static string BuildTextFileName(string content)
    {
        var firstLine = content
            .Split('\n', StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "text";

        var preview = firstLine.Length > 30 ? firstLine[..30] : firstLine;

        // Keep only safe characters in the filename
        var safe = new string(preview
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray());

        safe = safe.Trim('-');
        if (safe.Length == 0) safe = "note";

        return safe + ".txt";
    }

    // ================================================================
    // DTOs — defined here to keep the file list exactly as specified
    // ================================================================

    public sealed class ShareListResponse
    {
        /// <summary>Caller's network id — shown in the frontend header.</summary>
        public string NetworkIp { get; init; } = string.Empty;

        /// <summary>Server UTC time — used to sync the countdown.</summary>
        public DateTime ServerTimeUtc { get; init; }

        public IReadOnlyList<ShareListItem> Items { get; init; }
            = Array.Empty<ShareListItem>();
    }

    public sealed class ShareListItem
    {
        public int Id { get; init; }

        /// <summary>"file" or "text".</summary>
        public string Type { get; init; } = string.Empty;

        public string? FileName { get; init; }
        public long? FileSize { get; init; }

        /// <summary>Short text preview (for the list view).</summary>
        public string? TextPreview { get; init; }

        /// <summary>6-digit code.</summary>
        public string? Code { get; init; }

        /// <summary>Uploader's name (device name or user-provided).</summary>
        public string? UploaderName { get; init; }

        /// <summary>Uploader's IP — the grouping key, shown in the list.</summary>
        public string? NetworkIp { get; init; }

        public DateTime CreatedAtUtc { get; init; }
        public DateTime ExpiresAtUtc { get; init; }
        public int DownloadCount { get; init; }
    }
}