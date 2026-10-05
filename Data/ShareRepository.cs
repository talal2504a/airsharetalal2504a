using System.Data;
using Dapper;
using AirShare.Models;
using Microsoft.Data.SqlClient;

namespace AirShare.Data;

/// <summary>
/// Saara SQL yahan hai. Har query PARAMETERIZED hai.
///
/// Parameterized = values @Name ke through jaate hain, string concatenation
/// se nahi. Isse SQL injection impossible ho jaata hai.
/// </summary>
public class ShareRepository
{
    private readonly string _connectionString;

    public ShareRepository(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Default set nahi hai. appsettings.json check karo.");
    }

    /// <summary>Naya SqlConnection bana deta hai. Har query ke liye naya — thread-safe.</summary>
    private IDbConnection CreateConnection() => new SqlConnection(_connectionString);

    // ================================================================
    // INSERT
    // ================================================================

    /// <summary>New share insert karta hai aur uska Id return karta hai.</summary>
    public async Task<int> InsertAsync(Share share)
    {
        const string sql = @"
            INSERT INTO dbo.Shares
                (NetworkIp, ShareType, FileName, StoredName, TextContent, FileSize, Code,
                 CreatedAt, ExpiresAt, UploaderName)
            VALUES
                (@NetworkIp, @ShareType, @FileName, @StoredName, @TextContent, @FileSize, @Code,
                 @CreatedAt, @ExpiresAt, @UploaderName);

            SELECT CAST(SCOPE_IDENTITY() AS INT);";

        using var conn = CreateConnection();

        // SCOPE_IDENTITY() se nayi row ka Id milta hai
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(sql, share));
    }

    // ================================================================
    // READ
    // ================================================================

    /// <summary>
    /// Caller ke network ke ACTIVE (non-expired) shares, newest first.
    ///
    /// NOTE: TextContent bhi SELECT karna ZAROORI hai — list endpoint uska
    /// preview banata hai. Ye column miss hua to NullReferenceException aata hai.
    /// </summary>
    public async Task<IReadOnlyList<Share>> GetActiveByNetworkAsync(
        string networkIp, DateTime nowUtc)
    {
        const string sql = @"
            SELECT Id, NetworkIp, UploaderName, ShareType, FileName, StoredName, TextContent,
                   FileSize, Code, CreatedAt, ExpiresAt, DownloadCount
            FROM dbo.Shares
            WHERE NetworkIp = @NetworkIp
              AND ExpiresAt > @NowUtc
            ORDER BY CreatedAt DESC;";

        using var conn = CreateConnection();

        var rows = await conn.QueryAsync<Share>(new CommandDefinition(sql, new
        {
            NetworkIp = networkIp,
            NowUtc = nowUtc
        }));

        return rows.AsList();
    }

    /// <summary>ID se ek share (TextContent samet). Download/text endpoints ke liye.</summary>
    public async Task<Share?> GetByIdAsync(int id)
    {
        const string sql = @"
            SELECT Id, NetworkIp, UploaderName, ShareType, FileName, StoredName, TextContent,
                   FileSize, Code, CreatedAt, ExpiresAt, DownloadCount
            FROM dbo.Shares
            WHERE Id = @Id;";

        using var conn = CreateConnection();

        return await conn.QuerySingleOrDefaultAsync<Share>(
            new CommandDefinition(sql, new { Id = id }));
    }

    /// <summary>
    /// Code se share lookup — cross-network download ke liye.
    /// Code uniqueness check bhi isi se hota hai.
    /// </summary>
    public async Task<Share?> GetByCodeAsync(string code)
    {
        const string sql = @"
            SELECT Id, NetworkIp, UploaderName, ShareType, FileName, StoredName, TextContent,
                   FileSize, Code, CreatedAt, ExpiresAt, DownloadCount
            FROM dbo.Shares
            WHERE Code = @Code;";

        using var conn = CreateConnection();

        return await conn.QuerySingleOrDefaultAsync<Share>(
            new CommandDefinition(sql, new { Code = code }));
    }

    // ================================================================
    // COUNT
    // ================================================================

    /// <summary>Is IP ke kitne active shares hain (10-item limit check ke liye).</summary>
    public async Task<int> CountActiveByNetworkAsync(string networkIp, DateTime nowUtc)
    {
        const string sql = @"
            SELECT COUNT(*)
            FROM dbo.Shares
            WHERE NetworkIp = @NetworkIp
              AND ExpiresAt > @NowUtc;";

        using var conn = CreateConnection();

        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(sql, new
        {
            NetworkIp = networkIp,
            NowUtc = nowUtc
        }));
    }

    // ================================================================
    // UPDATE
    // ================================================================

    /// <summary>Download count 1 se badha deta hai.</summary>
    public async Task IncrementDownloadAsync(int id)
    {
        const string sql = @"
            UPDATE dbo.Shares
            SET DownloadCount = DownloadCount + 1
            WHERE Id = @Id;";

        using var conn = CreateConnection();

        await conn.ExecuteAsync(new CommandDefinition(sql, new { Id = id }));
    }

    // ================================================================
    // DELETE
    // ================================================================

    /// <summary>ID se delete (cleanup service ke liye).</summary>
    public async Task<int> DeleteByIdAsync(int id)
    {
        const string sql = "DELETE FROM dbo.Shares WHERE Id = @Id;";

        using var conn = CreateConnection();

        return await conn.ExecuteAsync(new CommandDefinition(sql, new { Id = id }));
    }

    /// <summary>
    /// Manual delete — sirf wahi IP jisne upload kiya.
    /// IP match nahi hua to 0 rows delete (0 return) — security check.
    /// </summary>
    public async Task<int> DeleteByIdAndNetworkAsync(int id, string networkIp)
    {
        const string sql = @"
            DELETE FROM dbo.Shares
            WHERE Id = @Id AND NetworkIp = @NetworkIp;";

        using var conn = CreateConnection();

        return await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            Id = id,
            NetworkIp = networkIp
        }));
    }

    /// <summary>
    /// Expired shares (cleanup service ke liye).
    /// StoredName zaroori hai taaki disk se file delete ho sake.
    /// TextContent ki zaroorat nahi — cleanup me content use nahi hota.
    /// </summary>
    public async Task<IReadOnlyList<Share>> GetExpiredAsync(DateTime nowUtc)
    {
        const string sql = @"
            SELECT Id, NetworkIp, UploaderName, ShareType, FileName, StoredName, FileSize, Code,
                   CreatedAt, ExpiresAt, DownloadCount
            FROM dbo.Shares
            WHERE ExpiresAt <= @NowUtc;";

        using var conn = CreateConnection();

        var rows = await conn.QueryAsync<Share>(
            new CommandDefinition(sql, new { NowUtc = nowUtc }));

        return rows.AsList();
    }
}