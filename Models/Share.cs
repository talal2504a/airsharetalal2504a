namespace AirShare.Models;

/// <summary>
/// Ek shared item ka DB record.
/// File ka data disk pe hota hai — yahan sirf metadata hai.
/// </summary>
public class Share
{
    // ---- Primary key (DB identity se aata hai) ----
    public int Id { get; set; }

    /// <summary>
    /// Uploader ka public IP.
    /// IPv6 ho to pehle se /64 prefix tak normalize hota hai.
    /// Isi field se grouping hoti hai — yahi core concept hai.
    /// </summary>
    public string NetworkIp { get; set; } = string.Empty;

    /// <summary>
    /// Uploader ka naam. Device name se auto-fill hota hai,
    /// user manually badal bhi sakta hai.
    /// </summary>
    public string? UploaderName { get; set; }

    /// <summary>"file" ya "text". Sirf ye 2 values allowed (DB me CHECK constraint hai).</summary>
    public string ShareType { get; set; } = string.Empty;

    /// <summary>User ka original file name. Sirf download header me dikhta hai, disk path me KABHI use nahi hota.</summary>
    public string? FileName { get; set; }

    /// <summary>Disk pe file ka naam — 32-char GUID ("N" format) + sanitized extension. Jaise "a3f9...c2.txt"</summary>
    public string? StoredName { get; set; }

    /// <summary>Text share ka poora content. File shares me NULL rehta hai.</summary>
    public string? TextContent { get; set; }

    /// <summary>Bytes me size. Text ke liye UTF-8 byte count.</summary>
    public long? FileSize { get; set; }

    /// <summary>
    /// 6-digit code — cross-network download ke liye.
    /// DB me UNIQUE filtered index hai, isliye duplicate nahi ho sakta.
    /// </summary>
    public string? Code { get; set; }

    /// <summary>UTC me banne ka time. Saari calculations UTC me hoti hain.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>UTC me expire hone ka time. Iske baad row + file dono delete.</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>Kitni baar download hua. Har download pe increment hota hai.</summary>
    public int DownloadCount { get; set; }

    // ---- Shortcuts (C# me computed properties) ----

    /// <summary>True agar ye file share hai.</summary>
    public bool IsFile => string.Equals(ShareType, "file", StringComparison.Ordinal);

    /// <summary>True agar ye text share hai.</summary>
    public bool IsText => string.Equals(ShareType, "text", StringComparison.Ordinal);
}