using System.Text;

namespace AirShare.Services;

/// <summary>Disk pe save ho chuki file ka metadata.</summary>
public sealed class StoredFile
{
    /// <summary>Disk pe hua naam — 32-char GUID + extension. DB me yahi jata hai.</summary>
    public required string StoredName { get; init; }

    /// <summary>User ka original naam (sanitized). Sirf download header me dikhta hai.</summary>
    public required string OriginalName { get; init; }

    /// <summary>File size bytes me.</summary>
    public required long Size { get; init; }
}

public interface IFileStorageService
{
    /// <summary>Absolute path jaha files save hoti hain.</summary>
    string RootPath { get; }

    /// <summary>Max allowed file size (bytes) — appsettings se aata hai.</summary>
    long MaxFileSizeBytes { get; }

    /// <summary>Blocked extensions list — appsettings se.</summary>
    IReadOnlyList<string> BlockedExtensions { get; }

    Task<StoredFile> SaveAsync(IFormFile file, CancellationToken ct = default);

    /// <summary>Path traversal protection ke saath full path nikalta hai.</summary>
    bool TryGetPath(string storedName, out string fullPath);

    /// <summary>Disk se file delete karta hai. Missing file / error pe crash nahi hota.</summary>
    void DeleteQuietly(string storedName);
}

/// <summary>
/// File disk pe save karta hai — GUID naam + sanitized extension.
/// User ka original filename KABHI disk path me use nahi hota.
/// </summary>
public class FileStorageService : IFileStorageService
{
    // Sirf disk pe bhejne wale format: 32 hex char (GUID "N") + optional extension
    private static readonly System.Text.RegularExpressions.Regex StoredNamePattern =
        new(@"^[a-f0-9]{32}(\.[a-z0-9]{1,12})?$",
            System.Text.RegularExpressions.RegexOptions.Compiled |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private readonly ILogger<FileStorageService> _logger;

    public string RootPath { get; }
    public long MaxFileSizeBytes { get; }
    public IReadOnlyList<string> BlockedExtensions { get; }

    public FileStorageService(
        IConfiguration config,
        IWebHostEnvironment environment,
        ILogger<FileStorageService> logger)
    {
        _logger = logger;

        MaxFileSizeBytes = config.GetValue<long>("AirShare:MaxFileSizeBytes", 25L * 1024 * 1024);

        BlockedExtensions = (config.GetSection("AirShare:BlockedExtensions").Get<string[]>()
            ?? Array.Empty<string>())
            .Select(e => e.ToLowerInvariant())
            .ToArray();

        var relative = config.GetValue("AirShare:UploadsFolder", "App_Data/Uploads")!;

        // ContentRoot se join karo — taaki files wwwroot ke BAHAR rahein.
        // (wwwroot ke andar hoti to static middleware se direct serve ho jayengi!)
        RootPath = Path.GetFullPath(Path.Combine(environment.ContentRootPath, relative));

        // Folder abhi bana do — permission issue pehle se pata chal jayega
        Directory.CreateDirectory(RootPath);

        _logger.LogInformation(
            "Uploads folder ready: {Path} (max {MaxBytes} bytes)", RootPath, MaxFileSizeBytes);
    }

    /// <summary>
    /// File disk pe likhta hai. Size aur extension pehle validate karta hai.
    /// </summary>
    public async Task<StoredFile> SaveAsync(IFormFile file, CancellationToken ct = default)
    {
        if (file.Length <= 0)
        {
            throw new InvalidOperationException("File khaali hai.");
        }

        if (file.Length > MaxFileSizeBytes)
        {
            throw new InvalidOperationException(
                $"File bahut badi hai. Max {MaxFileSizeBytes / (1024 * 1024)} MB.");
        }

        var originalName = SanitizeDisplayName(file.FileName);
        var extension = SanitizeExtension(file.FileName);

        // Dangerous extension block (exe, bat, ps1, vbs...)
        if (BlockedExtensions.Contains(extension))
        {
            throw new InvalidOperationException($"'{extension}' files block hain.");
        }

        // Guid.NewGuid() — user se koi bhi input naam me nahi jaata
        var storedName = Guid.NewGuid().ToString("N") + extension;
        var fullPath = Path.Combine(RootPath, storedName);

        await using (var stream = new FileStream(
            fullPath,
            FileMode.CreateNew,              // CreateNew: race condition se bachne ke liye
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            useAsync: true))
        {
            await file.CopyToAsync(stream, ct);
        }

        return new StoredFile
        {
            StoredName = storedName,
            OriginalName = originalName,
            Size = file.Length
        };
    }

    /// <summary>
    /// Path traversal protection.
    ///
    /// User input (StoredName) ko blindly Path.Combine karna
    /// "../../web.config" jaise attacks ko allow karta hai.
    /// Isliye: (1) regex se format validate (2) full path ke baad dobara check.
    /// </summary>
    public bool TryGetPath(string storedName, out string fullPath)
    {
        fullPath = string.Empty;

        // Sirf GUID + extension ka format hi maano
        if (string.IsNullOrWhiteSpace(storedName) || !StoredNamePattern.IsMatch(storedName))
        {
            return false;
        }

        var candidate = Path.GetFullPath(Path.Combine(RootPath, storedName));
        var rootWithSeparator = RootPath.EndsWith(Path.DirectorySeparatorChar)
            ? RootPath
            : RootPath + Path.DirectorySeparatorChar;

        // Candidate root ke ANDAR hona chahiye
        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // File name exactly wahi hona chahiye jo DB me tha
        if (!string.Equals(Path.GetFileName(candidate), storedName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        fullPath = candidate;
        return true;
    }

    /// <summary>
    /// Disk se file delete karta hai. File missing ho ya error aaye to
    /// chup chaap log karta hai — crash nahi karta (cleanup service safe rahe).
    /// </summary>
    public void DeleteQuietly(string storedName)
    {
        if (!TryGetPath(storedName, out var path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            // File lock hai ya permission issue — agli cycle me retry hoga
            _logger.LogWarning(ex, "Disk se file delete nahi hui: {StoredName}", storedName);
        }
    }

    /// <summary>
    /// Sirf display/download ke liye naam sanitize karta hai.
    /// Path separators aur control characters hata deta hai.
    /// </summary>
    private static string SanitizeDisplayName(string? original)
    {
        if (string.IsNullOrWhiteSpace(original))
        {
            return "download";
        }

        // Path.GetFileName se "C:\evil\a.txt" -> "a.txt"
        var name = Path.GetFileName(original);

        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsControl(ch)) continue;
            if (ch is '"' or '\'' or '\\' or '/') continue;
            sb.Append(ch);
        }

        var clean = sb.ToString().Trim();
        if (clean.Length == 0) clean = "download";

        // DB column NVARCHAR(255) hai —255 se lamba kat do
        return clean.Length > 255 ? clean[..255] : clean;
    }

    /// <summary>
    /// Extension me sirf alphanumeric rakhte hain.
    /// "....exe" aur "a.e&lt;&gt;xe" dono sanitize ho jaate hain.
    /// </summary>
    private static string SanitizeExtension(string? original)
    {
        var ext = Path.GetExtension(original ?? string.Empty).ToLowerInvariant();

        var sb = new StringBuilder(ext.Length);
        foreach (var ch in ext)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        }

        if (sb.Length == 0 || sb.Length > 12) return string.Empty;

        return "." + sb.ToString();
    }
}