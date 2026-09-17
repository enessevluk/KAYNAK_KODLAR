using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;

if (args.Length >= 4 && args[0].Equals("--verify-server-update", StringComparison.OrdinalIgnoreCase))
{
    var valid = ServerUpdateSecurity.VerifyPackage(args[1], args[2], args[3], out var verifyError);
    if (valid)
    {
        Console.WriteLine("SERVER_UPDATE_SIGNATURE_OK");
    }
    else
    {
        Console.Error.WriteLine(verifyError);
    }

    // Top-level Program.cs icinde `return` kullanmak sentezlenen Main donus tipini
    // etkileyebiliyor. Environment.Exit ile dogrulama modunu burada kesin olarak bitir.
    Environment.Exit(valid ? 0 : 12);
}

var builder = WebApplication.CreateBuilder(args);

// Konsolda normal şekilde çalışmaya devam eder; Windows Service Control Manager
// tarafından başlatıldığında servis ömrü ve durdurma sinyalleriyle bütünleşir.
builder.Host.UseWindowsService(options => options.ServiceName = "RavenMapServer");

// Konsolda yalnizca Raven'in anlamli durum satirlari ve gercek uyarilar gorunsun.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// SERVER\Config\raven_server.json is the owner-editable production configuration.
// The BAT file only points Raven.Server to this file; it never asks Shopier/product questions.
var externalConfigPath = Environment.GetEnvironmentVariable("RAVEN_CONFIG_FILE");
if (string.IsNullOrWhiteSpace(externalConfigPath))
{
    // VDS taşınırken RAVEN_CONFIG_FILE unutulsa bile yaygın owner config
    // konumlarını otomatik bul. İlk bulunan dosya appsettings'i override eder.
    var configCandidates = new[]
    {
        // Owner config her zaman SERVER\Config altindadir. App icindeki stale bir kopya
        // owner config'i asla ezmemeli.
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Config", "raven_server.json")),
        Path.Combine(builder.Environment.ContentRootPath, "Config", "raven_server.json"),
        Path.Combine(builder.Environment.ContentRootPath, "raven_server.json"),
        Path.Combine(AppContext.BaseDirectory, "Config", "raven_server.json")
    };
    externalConfigPath = configCandidates.FirstOrDefault(File.Exists);
}
if (!string.IsNullOrWhiteSpace(externalConfigPath))
{
    externalConfigPath = Path.GetFullPath(externalConfigPath);
    if (!File.Exists(externalConfigPath))
        throw new FileNotFoundException("Raven server config bulunamadi.", externalConfigPath);
    try
    {
        builder.Configuration.AddJsonFile(externalConfigPath, optional: false, reloadOnChange: false);
        Console.WriteLine($"[CONFIG] Owner config: {externalConfigPath}");
    }
    catch (InvalidDataException ex)
    {
        Console.Error.WriteLine("[CONFIG] HATA: raven_server.json gecersiz JSON.");
        Console.Error.WriteLine($"[CONFIG] Dosya: {externalConfigPath}");
        Console.Error.WriteLine("[CONFIG] Ilk kurulum paketini yeniden olusturun veya Admin/owner config yedeginizi geri yukleyin.");
        throw new InvalidOperationException($"Raven owner config JSON okunamadi: {externalConfigPath}", ex);
    }
}

var serverRoot = Environment.GetEnvironmentVariable("RAVEN_SERVER_ROOT");
if (string.IsNullOrWhiteSpace(serverRoot))
{
    var contentRoot = Path.GetFullPath(builder.Environment.ContentRootPath);
    var parentRoot = Directory.GetParent(contentRoot)?.FullName;
    // Taşınabilir VDS yapısı: SERVER\App veya SERVER\Build altında çalışan uygulama,
    // owner Config/Secrets/Data klasörlerini bir üst SERVER kökünde otomatik bulur.
    if (!string.IsNullOrWhiteSpace(parentRoot) &&
        (Directory.Exists(Path.Combine(parentRoot, "Secrets")) ||
         File.Exists(Path.Combine(parentRoot, "Config", "raven_server.json"))))
        serverRoot = parentRoot;
    else
        serverRoot = contentRoot;
}
serverRoot = Path.GetFullPath(serverRoot);

var raven = builder.Configuration.GetSection("Raven");
var configuredEnvironment = (raven["Environment"] ?? "").Trim();
var isDevelopment = string.IsNullOrWhiteSpace(configuredEnvironment)
    ? builder.Environment.IsDevelopment()
    : configuredEnvironment.Equals("Development", StringComparison.OrdinalIgnoreCase);
var publicBaseUrl = (raven["PublicBaseUrl"] ?? "http://127.0.0.1:5088").TrimEnd('/');
var listenUrl = raven["ListenUrl"] ?? "http://127.0.0.1:5088";
var dataDir = ServerHelpers.ResolveRavenPath(serverRoot, raven["DataDirectory"], "Data");
var updateDir = ServerHelpers.ResolveRavenPath(serverRoot, raven["UpdateDirectory"], "Updates");
var prefabContentDir = ServerHelpers.ResolveRavenPath(serverRoot, raven["PrefabContentDirectory"], "Content/Prefabs");
var privateKeySetting = Environment.GetEnvironmentVariable("RAVEN_LICENSE_PRIVATE_KEY_FILE")
    ?? raven["LicensePrivateKeyPath"];
var privateKeyPath = ServerHelpers.ResolveRavenPath(serverRoot, privateKeySetting, "Secrets/license_private.pem");
var updatePublicKeyPath = ServerHelpers.ResolveRavenPath(serverRoot, raven["UpdatePublicKeyPath"], "Secrets/update_public.pem");
var adminUser = raven["AdminUser"] ?? "ravenadmin";
var adminPassword = Environment.GetEnvironmentVariable("RAVEN_ADMIN_PASSWORD");
var adminTotpSecret = Environment.GetEnvironmentVariable("RAVEN_ADMIN_TOTP_SECRET") ?? "";
var gracePeriodHours = double.TryParse(raven["GracePeriodHours"], NumberStyles.Float, CultureInfo.InvariantCulture, out var graceHoursValue)
    ? Math.Clamp(graceHoursValue, 0, 168)
    : 24d;
var gracePeriod = TimeSpan.FromHours(gracePeriodHours);

// Owner tarafinda normal operasyon ayarlari tek dosyadan yönetilir.
// BAT/PowerShell business setting tasimaz; Admin Panel ayni owner config'i güvenli biçimde günceller.
if (string.IsNullOrWhiteSpace(externalConfigPath))
    throw new InvalidOperationException("Owner config bulunamadı. SERVER\\Config\\raven_server.json dosyasını kontrol edin.");
var ownerSettingsService = new OwnerSettingsService(externalConfigPath);
var ownerSettings = ownerSettingsService.Load();
var initialShopier = OwnerSettingsService.ToRuntimeShopier(ownerSettings);
var shopierValidationErrors = OwnerSettingsService.ValidateBusinessSettings(ownerSettings);

var shopierEnabled = initialShopier.Enabled;
if (shopierEnabled && shopierValidationErrors.Count > 0)
{
    Console.WriteLine("[SHOPIER] AYAR HATASI: " + string.Join(" | ", shopierValidationErrors));
    Console.WriteLine("[SHOPIER] Shopier geçici olarak PASİF. Raven Server çalışmaya devam edecek; Admin Panel > Sistem Ayarları bölümünden düzeltin.");
    initialShopier = initialShopier with { Enabled = false };
    shopierEnabled = false;
}
else if (shopierEnabled)
{
    Console.WriteLine($"[SHOPIER] Owner config yüklendi. PAT uzunluğu: {initialShopier.ApiKey.Length} karakter.");
}

var shopierBuyUrl = initialShopier.BuyUrl;
var shopierApiUrl = initialShopier.ApiUrl;
var shopierApiKey = initialShopier.ApiKey;
var shopierCheckIntervalSeconds = initialShopier.CheckIntervalSeconds;
var shopierRequestTimeoutSeconds = initialShopier.RequestTimeoutSeconds;
var shopierPageSize = initialShopier.PageSize;
var shopierMaximumPages = initialShopier.MaximumPages;
var shopierMaximumBackoffSeconds = initialShopier.MaximumBackoffSeconds;
var shopierProcessExistingOrdersOnFirstRun = initialShopier.ProcessExistingOrdersOnFirstRun;
var shopierProducts = initialShopier.Products.ToList();

if (!Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var configuredPublicUri) ||
    (configuredPublicUri.Scheme != Uri.UriSchemeHttp && configuredPublicUri.Scheme != Uri.UriSchemeHttps))
    throw new InvalidOperationException("Raven:PublicBaseUrl geçerli bir http:// veya https:// adresi olmalıdır.");
if (publicBaseUrl.Contains("VDS_IP_ADRESIN", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Raven:PublicBaseUrl hâlâ VDS_IP_ADRESIN placeholder değerinde. RAVEN.bat > 6 ILK KURULUM / VDS ADRESI ile gerçek VDS adresini girin.");

// Test modunda Steam URL'leri gelen istegin scheme/host bilgisinden uretilir.
// Bu kontrol yalnizca owner'a yanlis/stale config'i erkenden gosterir; uygulama yine kendini toparlar.
if (isDevelopment &&
    Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var devPublicUri) &&
    Uri.TryCreate(listenUrl, UriKind.Absolute, out var devListenUri) &&
    devPublicUri.Scheme == Uri.UriSchemeHttps && devListenUri.Scheme == Uri.UriSchemeHttp)
{
    Console.WriteLine($"[Raven][UYARI] Development modunda PublicBaseUrl HTTPS ({publicBaseUrl}) fakat Kestrel HTTP dinliyor ({listenUrl}). Steam test URL'leri gelen HTTP isteginden otomatik uretilecek.");
}

if (string.IsNullOrWhiteSpace(adminPassword))
    throw new InvalidOperationException("RAVEN_ADMIN_PASSWORD environment variable is required.");
if (!isDevelopment)
{
    if (string.IsNullOrWhiteSpace(adminTotpSecret))
        throw new InvalidOperationException("Production ortamında RAVEN_ADMIN_TOTP_SECRET zorunludur.");
    if (!Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var publicUri) || publicUri.Scheme != Uri.UriSchemeHttps)
        throw new InvalidOperationException("Production ortamında Raven:PublicBaseUrl mutlaka HTTPS olmalıdır.");
    if (shopierEnabled && string.IsNullOrWhiteSpace(shopierApiKey))
        throw new InvalidOperationException("Shopier etkinse Shopier:ApiKey zorunludur.");
    if (shopierEnabled && shopierProducts.Count == 0)
        throw new InvalidOperationException("Shopier etkinse Config\\raven_server.json icinde en az bir Products kaydi zorunludur.");
    if (shopierEnabled && (!Uri.TryCreate(shopierApiUrl, UriKind.Absolute, out var apiUri) || apiUri.Scheme != Uri.UriSchemeHttps))
        throw new InvalidOperationException("Shopier etkinse Shopier:ApiUrl HTTPS olmalıdır.");
    if (shopierEnabled && (!Uri.TryCreate(shopierBuyUrl, UriKind.Absolute, out var buyUri) || buyUri.Scheme != Uri.UriSchemeHttps))
        throw new InvalidOperationException("Shopier etkinse Shopier:BuyUrl HTTPS olmalıdır.");
}
Directory.CreateDirectory(dataDir);
Directory.CreateDirectory(updateDir);
Directory.CreateDirectory(prefabContentDir);

// Admin "beni hatırla" çerezi server yeniden başlasa bile geçerli kalsın.
// Anahtarlar SERVER\Data altında tutulur; App klasörü güncellenirken kaybolmaz.
var dataProtectionDir = Path.Combine(dataDir, "DataProtectionKeys");
Directory.CreateDirectory(dataProtectionDir);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionDir))
    .SetApplicationName("Raven.Server.Admin");

// Her Raven veritabani benzersiz bir sunucu kimligine sahiptir.
// Client aktivasyon cache'i bu kimlige baglanir; yeni/temiz bir DB eski aktivasyonu kabul etmez.
var db = new RavenDb(Path.Combine(dataDir, "raven.db"));
db.Initialize();
var serverInstanceId = db.GetServerInstanceId();
var serverVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.7.0";
const int serverProtocolVersion = 2;

builder.WebHost.UseUrls(listenUrl);
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.LoginPath = "/admin/login";
    o.Cookie.Name = "Raven.Admin." + serverInstanceId[..Math.Min(12, serverInstanceId.Length)];
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.SecurePolicy = isDevelopment ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.SlidingExpiration = true;
});
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(o =>
{
    o.FormFieldName = "__RequestVerificationToken";
    o.Cookie.Name = "Raven.Admin.Csrf." + serverInstanceId[..Math.Min(12, serverInstanceId.Length)];
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.SecurePolicy = isDevelopment ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});
builder.Services.AddRateLimiter(o =>
{
    o.AddPolicy("api", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 120,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    o.AddPolicy("admin-login", context => RateLimitPartition.GetFixedWindowLimiter(
        "admin:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 8,
            Window = TimeSpan.FromMinutes(5),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});
builder.Services.AddHttpClient("steam", c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddHttpClient("shopier");

var app = builder.Build();
var consoleMonitor = new RavenConsoleMonitor(Path.Combine(dataDir, "Logs"));
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});
if (!isDevelopment)
{
    app.UseHsts();
    app.Use(async (ctx, next) =>
    {
        // Kestrel reverse proxy arkasinda yerel HTTP dinleyebilir; dis istemci trafigi
        // ise her zaman owner config'teki tek kanonik HTTPS domaine yonlendirilir.
        if (!ctx.Request.IsHttps)
        {
            ctx.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
            ctx.Response.Headers.Location = publicBaseUrl + ctx.Request.PathBase + ctx.Request.Path + ctx.Request.QueryString;
            return;
        }
        await next();
    });
}
app.Use((ctx, next) => consoleMonitor.HandleAsync(ctx, next));
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Every state-changing admin request must originate from a Raven admin page.
app.Use(async (ctx, next) =>
{
    if (HttpMethods.IsPost(ctx.Request.Method) && ctx.Request.Path.StartsWithSegments("/admin"))
    {
        try
        {
            await ctx.RequestServices.GetRequiredService<Microsoft.AspNetCore.Antiforgery.IAntiforgery>().ValidateRequestAsync(ctx);
        }
        catch (Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            await ctx.Response.WriteAsync("Admin güvenlik doğrulaması başarısız. Sayfayı yenileyip işlemi tekrar deneyin.");
            return;
        }
    }
    await next();
});

// Admin/API/Steam sayfalari eski tarayici cache'inden servis edilmez.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/admin") ||
        ctx.Request.Path.StartsWithSegments("/api") ||
        ctx.Request.Path.StartsWithSegments("/auth/steam"))
    {
        ctx.Response.OnStarting(() =>
        {
            ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
            ctx.Response.Headers["Pragma"] = "no-cache";
            ctx.Response.Headers["Expires"] = "0";
            return Task.CompletedTask;
        });
    }
    await next();
});

var signer = new OfflineTokenSigner(privateKeyPath);
var prefabContent = new PrefabContentService(prefabContentDir, signer);
var updateVerifier = new UpdatePackageVerifier(updatePublicKeyPath);
var steam = new SteamOpenIdService(app.Services.GetRequiredService<IHttpClientFactory>());
var shopierPoller = new ShopierOrderPoller(
    app.Services.GetRequiredService<IHttpClientFactory>(), db, initialShopier, gracePeriod);
// Poller her zaman ayakta kalir. Admin Panel'den Shopier acilip kapatildiginda server restart gerekmez.
_ = shopierPoller.RunAsync(app.Lifetime.ApplicationStopping);

var runtime = new ServerRuntime
{
    Db = db,
    Signer = signer,
    PrefabContent = prefabContent,
    UpdateVerifier = updateVerifier,
    Steam = steam,
    ShopierPoller = shopierPoller,
    OwnerSettings = ownerSettingsService,
    PublicBaseUrl = publicBaseUrl,
    AdminUser = adminUser,
    AdminPassword = adminPassword,
    AdminTotpSecret = adminTotpSecret,
    UpdateDirectory = updateDir,
    ServerInstanceId = serverInstanceId,
    ServerVersion = serverVersion,
    GracePeriod = gracePeriod,
    ServerProtocolVersion = serverProtocolVersion,
    IsDevelopment = isDevelopment
};
runtime.ApplyOwnerSettings(ownerSettings, initialShopier);

PublicEndpoints.Map(app, runtime);
ApiEndpoints.Map(app, runtime);
AdminEndpoints.Map(app, runtime);

consoleMonitor.PrintBanner(serverVersion, BuildStamp.Id, listenUrl, publicBaseUrl, isDevelopment, runtime.ShopierEnabled);
app.Lifetime.ApplicationStarted.Register(() => consoleMonitor.PrintReady(publicBaseUrl));
app.Lifetime.ApplicationStopping.Register(consoleMonitor.PrintStopping);
_ = consoleMonitor.RunStatusLoopAsync(runtime, app.Lifetime.ApplicationStopping);

app.Run();
