namespace AirShare.Models;

/// <summary>
/// POST /api/upload ka response body.
/// Frontend isi JSON ko padhta hai aur result panel + code dikhata hai.
/// </summary>
public class UploadResult
{
    /// <summary>Request successful thi ya nahi. Frontend iske hisaab se toast dikhata hai.</summary>
    public bool Success { get; set; }

    /// <summary>
    /// User ko dikhane wali message.
    /// Error me bhi yahi field aata hai — frontend generic message nahi, ye dikhata hai.
    /// Jaise: "File bahut badi hai. Max 25 MB." ya "Is network pe already 10 active items hain."
    /// </summary>
    public string? Message { get; set; }

    /// <summary>Naya share ka ID. Manual delete karne ke liye kaam aata hai.</summary>
    public int? Id { get; set; }

    /// <summary>
    /// 6-digit code — doosre network se download karne ke liye.
    /// Upload ke baad frontend ise "Other-network code" box me dikhata hai.
    /// </summary>
    public string? Code { get; set; }

    /// <summary>UTC expiry time. Countdown isi se chalta hai.</summary>
    public DateTime? ExpiresAt { get; set; }
}