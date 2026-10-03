using System.Threading.RateLimiting;
using AirShare.Data;
using AirShare.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// =====================================================================
// 0) Shared config values — size limits aur rate limits
// =====================================================================
var maxFileSizeBytes = builder.Configuration
    .GetValue("AirShare:MaxFileSizeBytes", 25L * 1024 * 1024);

var uploadLimitPerMin = builder.Configuration
    .GetValue("AirShare:UploadRateLimitPerMinute", 10);

var downloadLimitPerMin = builder.Configuration
    .GetValue("AirShare:DownloadRateLimitPerMinute", 60);

builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});

// =====================================================================
// 1) Controllers + JSON
// =====================================================================
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy =
            System.Text.Json.JsonNamingPolicy.CamelCase;
    });

// =====================================================================
// 2) SIZE LIMIT #1 — FormOptions (multipart body read limit)
//    ASP.NET decide karta hai kitna bada form padh sakta hai.
// =====================================================================
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxFileSizeBytes;

    // Text payload FormData fields ke through aata hai
    options.ValueLengthLimit = (int)maxFileSizeBytes;
    options.ValueCountLimit = 16;

    // 64 KB tak memory me, uske baad disk pe
    options.MemoryBufferThreshold = 64 * 1024;
});

// =====================================================================
// 3) SIZE LIMIT #2 — Kestrel MaxRequestBodySize
//    Raw request body ki limit.
// =====================================================================
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maxFileSizeBytes;

    // Server version leak mat karo
    options.AddServerHeader = false;
});

// =====================================================================
// 4) Services — Singleton (ye stateless hain)
// =====================================================================
builder.Services.AddSingleton<IClientIpService, ClientIpService>();
builder.Services.AddSingleton<IFileStorageService, FileStorageService>();
builder.Services.AddSingleton<ShareRepository>();
builder.Services.AddHostedService<CleanupService>();

// =====================================================================
// 5) Rate limiter — 10 uploads per minute PER IP
//    Partition key = normalized network id (IPv6 /64 grouped)
// =====================================================================
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("uploads", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ClientIpService.Normalize(context.Connection.RemoteIpAddress),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = uploadLimitPerMin,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));

    options.AddPolicy("downloads", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ClientIpService.Normalize(context.Connection.RemoteIpAddress),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = downloadLimitPerMin,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));

    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.Headers["Retry-After"] = "60";

        // JSON error body — frontend ko parse karne me asaani hoti hai
        if (context.HttpContext.Request.Path.StartsWithSegments("/api"))
        {
            context.HttpContext.Response.ContentType = "application/json";

            await context.HttpContext.Response.WriteAsync(
                """{"success":false,"message":"Bahut zyada requests. 1 minute baad try karo."}""",
                token);
        }
    };
});

// =====================================================================
var app = builder.Build();

// =====================================================================
// 6) FORWARDED HEADERS — MUST BE FIRST MIDDLEWARE
//
//    MonsterASP ke aage proxy/IIS hai. Bina iske SARI users ka IP
//    EK SAME dikhega aur app puri tarah kaam nahi karega.
//
//    Ye middleware sabse pehle hona chahiye — warna RemoteIpAddress
//    proxy ka IP dega, user ka nahi.
// =====================================================================
// Options object pehle banao (object initializer me ye properties// set nahi ho sakte — woh sirf constructor ya baad me assign hote hain)
var forwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor
        | ForwardedHeaders.XForwardedProto,
    ForwardLimit = 1,              // sirf apna trusted proxy add karega
    RequireHeaderSymmetry = false  // kuch proxies XFF ke saath port bhi bhejte hain
};

// SECURITY NOTE: ye lists clear karne ka matlab hai ki hum HAR incoming
// proxy par bharosa karte hain. Ye tabhi sahi hai jab hamesha apne
// hosting provider ke proxy ke peeche ho (MonsterASP ke liye haan).
// Apne server pe chalate ho to in lists me apne proxy IP daal do.
forwardedHeaders.KnownNetworks.Clear();
forwardedHeaders.KnownProxies.Clear();

app.UseForwardedHeaders(forwardedHeaders);

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

// =====================================================================
// 7) Static frontend (wwwroot)
//
//    App_Data wwwroot ke BAHAR hai — isliye uploads kabhi directly
//    serve nahi honge. Sirf frontend files serve hoti hain.
// =====================================================================
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        // index.html cache mat karo — updates turant dikhe
        if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers.CacheControl =
                "no-cache, no-store, must-revalidate";
        }
    }
});

// =====================================================================
// 8) Rate limiter + Controllers
// =====================================================================
app.UseRateLimiter();
app.MapControllers();

// Baaki sab kuch index.html pe bhejo (future routing ke liye)
app.MapFallbackToFile("index.html");

app.Run();