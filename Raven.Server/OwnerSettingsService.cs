using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

sealed class OwnerSettingsService
{
    private readonly object gate = new();
    private readonly string path;
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public OwnerSettingsService(string configPath)
    {
        path = Path.GetFullPath(configPath);
    }

    public string ConfigPath => path;

    public OwnerSettingsDocument Load()
    {
        lock (gate)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("Raven owner config bulunamadı.", path);

            var json = File.ReadAllText(path, Encoding.UTF8);
            var settings = JsonSerializer.Deserialize<OwnerSettingsDocument>(json, ReadOptions)
                           ?? throw new InvalidDataException("Raven owner config okunamadı.");
            settings.Raven ??= new RavenOwnerSettings();
            settings.Shopier ??= new ShopierOwnerSettings();
            settings.Shopier.Products ??= [];
            settings.Support ??= new SupportOwnerSettings();
            return settings;
        }
    }

    public OwnerSettingsDocument UpdateBusinessSettings(BusinessSettingsUpdate update)
    {
        lock (gate)
        {
            var current = LoadUnlocked();
            var next = ApplyUpdate(current, update);
            var errors = ValidateBusinessSettings(next);
            if (errors.Count > 0)
                throw new InvalidOperationException(string.Join("\n", errors));

            // JsonNode ile yalnız owner'ın yönetebildiği bölümleri değiştiriyoruz.
            // Raven/gelecekte eklenecek bilinmeyen alanlar korunur.
            var root = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject
                       ?? throw new InvalidDataException("Raven owner config JSON nesnesi değil.");

            root["Shopier"] = JsonSerializer.SerializeToNode(next.Shopier, WriteOptions);
            root["Support"] = JsonSerializer.SerializeToNode(next.Support, WriteOptions);

            var temp = path + ".tmp";
            var backup = path + ".backup";
            File.WriteAllText(temp, root.ToJsonString(WriteOptions), new UTF8Encoding(false));
            File.Copy(path, backup, true);
            File.Move(temp, path, true);
            return next;
        }
    }

    public static IReadOnlyList<string> ValidateBusinessSettings(OwnerSettingsDocument settings)
    {
        var errors = new List<string>();
        var shopier = settings.Shopier ?? new ShopierOwnerSettings();

        if (shopier.Enabled)
        {
            var key = (shopier.ApiKey ?? "").Trim();
            if (key.Length < 16) errors.Add("Shopier API anahtarı eksik veya çok kısa.");
            else if (key.Any(char.IsControl)) errors.Add("Shopier API anahtarı geçersiz kontrol karakteri içeriyor.");

            if (!IsHttps(shopier.ApiUrl)) errors.Add("Shopier API URL geçerli bir HTTPS adresi olmalıdır.");
            if (!IsHttps(shopier.BuyUrl)) errors.Add("Shopier satın alma URL geçerli bir HTTPS adresi olmalıdır.");

            var activeProducts = (shopier.Products ?? []).Where(x => x.Enabled).ToList();
            if (activeProducts.Count == 0) errors.Add("Shopier etkinse en az bir ürün tanımlı olmalıdır.");
            foreach (var p in activeProducts)
            {
                if (string.IsNullOrWhiteSpace(p.ProductId) && string.IsNullOrWhiteSpace(p.Title))
                    errors.Add($"{p.Plan} ürünü için ProductId veya ürün adı gereklidir.");
                if (!string.Equals(p.Plan, EntitlementPlans.Standard, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(p.Plan, EntitlementPlans.Premium, StringComparison.OrdinalIgnoreCase))
                    errors.Add($"Geçersiz Shopier planı: {p.Plan}");
            }

            var ids = activeProducts.Select(x => (x.ProductId ?? "").Trim()).Where(x => x.Length > 0).ToList();
            if (ids.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
                errors.Add("Aynı Shopier ProductId birden fazla üründe kullanılamaz.");
        }

        var supportUrl = (settings.Support?.Url ?? "").Trim();
        if (supportUrl.Length > 0 && !IsHttpOrHttps(supportUrl))
            errors.Add("Destek/Discord adresi geçerli bir http:// veya https:// URL olmalıdır.");

        return errors;
    }

    public static ShopierRuntimeConfig ToRuntimeShopier(OwnerSettingsDocument settings)
    {
        var s = settings.Shopier ?? new ShopierOwnerSettings();
        var products = (s.Products ?? [])
            .Where(x => x.Enabled && (!string.IsNullOrWhiteSpace(x.ProductId) || !string.IsNullOrWhiteSpace(x.Title)))
            .Select(x => new ShopierProductConfig(
                x.Enabled,
                (x.ProductId ?? "").Trim(),
                (x.Title ?? "").Trim(),
                (x.ExpectedUnitPrice ?? "").Trim(),
                string.IsNullOrWhiteSpace(x.ExpectedCurrency) ? "TRY" : x.ExpectedCurrency.Trim(),
                x.LicenseDays,
                EntitlementPlans.ParseConfigured(x.Plan)))
            .ToList();

        return new ShopierRuntimeConfig(
            s.Enabled,
            (s.ApiKey ?? "").Trim(),
            string.IsNullOrWhiteSpace(s.ApiUrl) ? "https://api.shopier.com/v1/orders" : s.ApiUrl.Trim(),
            (s.BuyUrl ?? "").Trim(),
            Math.Max(30, s.CheckIntervalSeconds),
            Math.Max(5, s.RequestTimeoutSeconds),
            Math.Clamp(s.PageSize, 1, 50),
            Math.Clamp(s.MaximumPagesPerCheck, 1, 100),
            Math.Max(Math.Max(30, s.CheckIntervalSeconds), s.MaximumErrorBackoffSeconds),
            s.ProcessExistingOrdersOnFirstRun,
            products);
    }

    private OwnerSettingsDocument LoadUnlocked()
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Raven owner config bulunamadı.", path);
        var settings = JsonSerializer.Deserialize<OwnerSettingsDocument>(File.ReadAllText(path, Encoding.UTF8), ReadOptions)
                       ?? throw new InvalidDataException("Raven owner config okunamadı.");
        settings.Raven ??= new RavenOwnerSettings();
        settings.Shopier ??= new ShopierOwnerSettings();
        settings.Shopier.Products ??= [];
        settings.Support ??= new SupportOwnerSettings();
        return settings;
    }

    private static OwnerSettingsDocument ApplyUpdate(OwnerSettingsDocument current, BusinessSettingsUpdate update)
    {
        current.Shopier ??= new ShopierOwnerSettings();
        current.Support ??= new SupportOwnerSettings();

        current.Shopier.Enabled = update.ShopierEnabled;
        if (!string.IsNullOrWhiteSpace(update.ShopierApiKeyReplacement))
            current.Shopier.ApiKey = update.ShopierApiKeyReplacement.Trim();
        current.Shopier.BuyUrl = (update.ShopierBuyUrl ?? "").Trim();
        current.Shopier.ApiUrl = string.IsNullOrWhiteSpace(update.ShopierApiUrl)
            ? "https://api.shopier.com/v1/orders"
            : update.ShopierApiUrl.Trim();
        current.Shopier.CheckIntervalSeconds = Math.Max(30, update.ShopierCheckIntervalSeconds);

        current.Shopier.Products ??= [];
        UpsertProduct(current.Shopier.Products, EntitlementPlans.Standard, update.Standard);
        UpsertProduct(current.Shopier.Products, EntitlementPlans.Premium, update.Premium);

        current.Support.Url = (update.SupportUrl ?? "").Trim();
        current.Support.Label = string.IsNullOrWhiteSpace(update.SupportLabel) ? "Discord Destek" : update.SupportLabel.Trim();
        current.Support.Message = string.IsNullOrWhiteSpace(update.SupportMessage)
            ? "Sorununuz devam ederse Raven destek ekibine ulaşabilirsiniz."
            : update.SupportMessage.Trim();
        return current;
    }

    private static void UpsertProduct(List<ShopierOwnerProductSettings> products, string plan, ProductSettingsUpdate update)
    {
        var p = products.FirstOrDefault(x => string.Equals(x.Plan, plan, StringComparison.OrdinalIgnoreCase));
        if (p is null)
        {
            p = new ShopierOwnerProductSettings { Plan = plan };
            products.Add(p);
        }
        p.Enabled = update.Enabled;
        p.ProductId = (update.ProductId ?? "").Trim();
        p.Title = (update.Title ?? "").Trim();
        p.ExpectedUnitPrice = (update.ExpectedUnitPrice ?? "").Trim();
        p.ExpectedCurrency = string.IsNullOrWhiteSpace(update.ExpectedCurrency) ? "TRY" : update.ExpectedCurrency.Trim();
        p.LicenseDays = update.LicenseDays;
        p.Plan = plan;
    }

    private static bool IsHttps(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private static bool IsHttpOrHttps(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

sealed class OwnerSettingsDocument
{
    public RavenOwnerSettings? Raven { get; set; } = new();
    public ShopierOwnerSettings? Shopier { get; set; } = new();
    public SupportOwnerSettings? Support { get; set; } = new();
}

sealed class RavenOwnerSettings
{
    public string PublicBaseUrl { get; set; } = "";
    public string ListenUrl { get; set; } = "";
    public string Environment { get; set; } = "Development";
}

sealed class ShopierOwnerSettings
{
    public bool Enabled { get; set; }
    public string ApiKey { get; set; } = "";
    public string BuyUrl { get; set; } = "";
    public string ApiUrl { get; set; } = "https://api.shopier.com/v1/orders";
    public int CheckIntervalSeconds { get; set; } = 60;
    public int RequestTimeoutSeconds { get; set; } = 20;
    public int PageSize { get; set; } = 50;
    public int MaximumPagesPerCheck { get; set; } = 20;
    public int MaximumErrorBackoffSeconds { get; set; } = 600;
    public List<ShopierOwnerProductSettings> Products { get; set; } = [];
    public bool ProcessExistingOrdersOnFirstRun { get; set; }
}

sealed class ShopierOwnerProductSettings
{
    public bool Enabled { get; set; } = true;
    public string ProductId { get; set; } = "";
    public string Title { get; set; } = "";
    public string ExpectedUnitPrice { get; set; } = "";
    public string ExpectedCurrency { get; set; } = "TRY";
    public int LicenseDays { get; set; }
    public string Plan { get; set; } = EntitlementPlans.Standard;
}

sealed class SupportOwnerSettings
{
    public string Url { get; set; } = "";
    public string Label { get; set; } = "Discord Destek";
    public string Message { get; set; } = "Sorununuz devam ederse Raven destek ekibine ulaşabilirsiniz.";
}

sealed record ProductSettingsUpdate(bool Enabled, string ProductId, string Title, string ExpectedUnitPrice, string ExpectedCurrency, int LicenseDays);
sealed record BusinessSettingsUpdate(
    bool ShopierEnabled,
    string ShopierApiKeyReplacement,
    string ShopierBuyUrl,
    string ShopierApiUrl,
    int ShopierCheckIntervalSeconds,
    ProductSettingsUpdate Standard,
    ProductSettingsUpdate Premium,
    string SupportUrl,
    string SupportLabel,
    string SupportMessage);

sealed record ShopierRuntimeConfig(
    bool Enabled,
    string ApiKey,
    string ApiUrl,
    string BuyUrl,
    int CheckIntervalSeconds,
    int RequestTimeoutSeconds,
    int PageSize,
    int MaximumPages,
    int MaximumBackoffSeconds,
    bool ProcessExistingOrdersOnFirstRun,
    IReadOnlyList<ShopierProductConfig> Products);
