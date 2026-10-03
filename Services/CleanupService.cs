using AirShare.Data;
using AirShare.Models;
using Microsoft.Data.SqlClient;

namespace AirShare.Services;

/// <summary>
/// Background service — har 60 second me expired shares hatata hai.
///
/// Logic:
///   1. Expired rows select karo (DB se)
///   2. Har file disk se delete karo
///   3. DB row delete karo
///
/// IMPORTANT: Agar file delete FAIL ho jaye to row delete NAHI karte —
/// taaki agli cycle me retry ho sake. Agar file pehle se missing hai to
/// row turant delete kar dete hain.
/// </summary>
public class CleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IFileStorageService _storage;
    private readonly ILogger<CleanupService> _logger;
    private readonly TimeSpan _interval;

    public CleanupService(
        IServiceScopeFactory scopeFactory,
        IFileStorageService storage,
        IConfiguration config,
        ILogger<CleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _storage = storage;
        _logger = logger;

        // Config se interval lo, lekin 10s-1hr ke beech force karo
        var seconds = config.GetValue("AirShare:CleanupIntervalSeconds", 60);
        _interval = TimeSpan.FromSeconds(Math.Clamp(seconds, 10, 3600));
    }

    /// <summary>
    /// Background service ka main loop. Ye tab tak chalta hai jab tak app band na ho.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Cleanup service shuru ho gayi. Interval = {Interval}", _interval);

        // Server start hote hi ek baar chalao — stale items turant clean honge
        await SafeCleanupAsync(stoppingToken);

        // PeriodicTimer — GC-friendly, Task.Delay loop se behtar
        using var timer = new PeriodicTimer(_interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SafeCleanupAsync(stoppingToken);
        }

        _logger.LogInformation("Cleanup service band ho gayi.");
    }

    /// <summary>
    /// Har cycle ka wrapper — koi bhi exception aaye to service crash nahi hona chahiye.
    /// (BackgroundService crash hota hai to poora app band ho jata hai.)
    /// </summary>
    private async Task SafeCleanupAsync(CancellationToken ct)
    {
        try
        {
            await CleanupAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Server shutdown ho raha hai — normal hai, error nahi
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cleanup cycle me error aa gaya.");
        }
    }

    /// <summary>
    /// Asli cleanup kaam: expired rows ka file + row delete karo.
    /// </summary>
    private async Task CleanupAsync(CancellationToken ct)
    {
        // Scoped service (ShareRepository) resolve karne ke liye scope chahiye
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ShareRepository>();

        var now = DateTime.UtcNow;

        // ---- 1. Expired rows fetch karo ----
        List<Share> expired;
        try
        {
            expired = (await repo.GetExpiredAsync(now)).ToList();
        }
        catch (SqlException ex)
        {
            // DB down hai — agli cycle me try karenge
            _logger.LogError(ex, "Expired rows fetch nahi ho paye.");
            return;
        }

        if (expired.Count == 0)
        {
            return; // kuch nahi karna
        }

        // ---- 2. Har item: file delete, phir row delete ----
        var deleted = 0;
        var keptForRetry = 0;

        foreach (var share in expired)
        {
            ct.ThrowIfCancellationRequested(); // shutdown pe ruk jaao

            if (share.IsFile && !string.IsNullOrEmpty(share.StoredName))
            {
                if (_storage.TryGetPath(share.StoredName, out var path) && File.Exists(path))
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch (Exception ex)
                    {
                        // File lock / permission issue — row rehne do, retry hoga
                        _logger.LogWarning(ex,
                            "Share #{Id} ki file delete nahi hui, agli cycle me retry hoga.",
                            share.Id);
                        keptForRetry++;
                        continue; // <- is row ko SKIP karo, DB row nahi delete
                    }
                }
                // File disk pe nahi hai (already deleted / manual delete) — row hata do
            }

            // File delete ho chuki (ya file thi hi nahi) — ab row delete karo
            await repo.DeleteByIdAsync(share.Id);
            deleted++;
        }

        _logger.LogInformation(
            "Cleanup: {Total} expired me se {Deleted} delete, {Retry} retry pending.",
            expired.Count, deleted, keptForRetry);
    }
}