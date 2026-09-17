using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

sealed class ShopierOrderPoller
{
    private static readonly Regex Steam64 = new(@"(?<!\d)(7656119\d{10})(?!\d)", RegexOptions.Compiled);
    private readonly IHttpClientFactory clients;
    private readonly RavenDb db;
    private readonly TimeSpan gracePeriod;
    private readonly DateTimeOffset baselineUtc;
    private readonly SemaphoreSlim gate = new(1, 1);
    private volatile ShopierRuntimeConfig settings;
    private int consecutiveFailures;

    public DateTimeOffset? LastSuccessfulPollUtc { get; private set; }
    public DateTimeOffset? NextCheckUtc { get; private set; }
    public string LastError { get; private set; } = "";
    public bool InitialSyncCompleted => db.IsShopierInitialSyncCompleted();
    public DateTimeOffset BaselineUtc => baselineUtc;
    public DateTimeOffset CursorUtc => db.GetShopierCursorUtc();
    public bool Enabled => settings.Enabled;
    public ShopierRuntimeConfig CurrentSettings => settings;

    public ShopierOrderPoller(IHttpClientFactory clients, RavenDb db, ShopierRuntimeConfig initialSettings, TimeSpan gracePeriod)
    {
        this.clients = clients;
        this.db = db;
        settings = initialSettings;
        this.gracePeriod = gracePeriod;
        baselineUtc = db.GetOrCreateShopierBaselineUtc();
    }

    public void UpdateConfiguration(ShopierRuntimeConfig next)
    {
        settings = next;
        consecutiveFailures = 0;
        LastError = "";
        NextCheckUtc = DateTimeOffset.UtcNow;
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var snapshot = settings;
            if (snapshot.Enabled)
                await CheckNowAsync(stoppingToken);
            else
                LastError = "";

            snapshot = settings;
            var delay = snapshot.Enabled ? GetDelaySeconds(snapshot) : 30;
            NextCheckUtc = DateTimeOffset.UtcNow.AddSeconds(delay);
            try { await Task.Delay(TimeSpan.FromSeconds(delay), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task CheckNowAsync(CancellationToken ct)
    {
        var snapshot = settings;
        if (!snapshot.Enabled)
        {
            LastError = "";
            return;
        }
        if (string.IsNullOrWhiteSpace(snapshot.ApiKey) || string.IsNullOrWhiteSpace(snapshot.ApiUrl))
        {
            LastError = "Shopier API anahtarı veya API URL eksik.";
            return;
        }
        if (snapshot.Products.Count == 0)
        {
            LastError = "Shopier ürün listesi boş.";
            return;
        }
        if (!await gate.WaitAsync(0, ct)) return;

        try
        {
            // Ayar değişikliği bir kontrolün ortasında olursa o kontrol kendi tutarlı snapshot'ı ile tamamlanır.
            snapshot = settings;
            if (!snapshot.Enabled) return;

            var pollCutoffUtc = DateTimeOffset.UtcNow;
            var initialSyncCompleted = db.IsShopierInitialSyncCompleted();
            DateTimeOffset? startUtc = null;
            if (!snapshot.ProcessExistingOrdersOnFirstRun || initialSyncCompleted)
            {
                var cursor = initialSyncCompleted ? db.GetShopierCursorUtc() : baselineUtc;
                startUtc = cursor.AddMinutes(-5);
            }

            var all = new List<JsonElement>();
            var lastPageWasFull = false;
            for (var page = 1; page <= snapshot.MaximumPages; page++)
            {
                var pageOrders = await RequestPageAsync(snapshot, page, startUtc, pollCutoffUtc.AddMinutes(1), ct);
                all.AddRange(pageOrders);
                lastPageWasFull = pageOrders.Count >= snapshot.PageSize;
                if (!lastPageWasFull) break;
            }

            if (lastPageWasFull)
            {
                var overflowProbe = await RequestPageAsync(snapshot, snapshot.MaximumPages + 1, startUtc, pollCutoffUtc.AddMinutes(1), ct);
                if (overflowProbe.Count > 0)
                    throw new InvalidOperationException($"Shopier sayfalama sınırına ulaşıldı ({snapshot.MaximumPages} x {snapshot.PageSize}). Cursor ilerletilmedi; MaximumPages değerini yükseltin.");
            }

            foreach (var order in all
                .Where(x => x.ValueKind == JsonValueKind.Object)
                .GroupBy(x => ShopierApiParser.GetString(x, "id") ?? "", StringComparer.OrdinalIgnoreCase)
                .Where(g => !string.IsNullOrWhiteSpace(g.Key))
                .Select(g => g.First())
                .Reverse())
            {
                ProcessOrder(snapshot, order);
            }

            if (!initialSyncCompleted)
                db.MarkShopierInitialSyncCompleted();
            db.SetShopierCursorUtc(pollCutoffUtc);

            consecutiveFailures = 0;
            LastError = "";
            LastSuccessfulPollUtc = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            consecutiveFailures++;
            LastError = ex.Message;
        }
        finally { gate.Release(); }
    }

    private async Task<List<JsonElement>> RequestPageAsync(ShopierRuntimeConfig snapshot, int page, DateTimeOffset? startUtc, DateTimeOffset endUtc, CancellationToken ct)
    {
        var client = clients.CreateClient("shopier");
        client.Timeout = TimeSpan.FromSeconds(snapshot.RequestTimeoutSeconds);
        using var req = new HttpRequestMessage(HttpMethod.Get, BuildPagedUrl(snapshot, page, startUtc, endUtc));
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", snapshot.ApiKey);
        req.Headers.Accept.ParseAdd("application/json");
        using var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if ((int)response.StatusCode == 429)
            throw new ShopierRateLimitException("Shopier API rate limit (429).", 120);
        if (!response.IsSuccessStatusCode)
        {
            var responseText = string.IsNullOrWhiteSpace(body) ? "<boş yanıt>" : Trim(body, 240);
            throw new InvalidOperationException($"Shopier API HTTP {(int)response.StatusCode} ({response.ReasonPhrase}): {responseText}");
        }
        return ShopierApiParser.ExtractOrders(body);
    }

    private static string BuildPagedUrl(ShopierRuntimeConfig snapshot, int page, DateTimeOffset? startUtc, DateTimeOffset endUtc)
    {
        var parts = new List<string>
        {
            "page=" + page.ToString(CultureInfo.InvariantCulture),
            "limit=" + snapshot.PageSize.ToString(CultureInfo.InvariantCulture),
            "sort=dateDesc"
        };

        if (startUtc is not null)
            parts.Add("dateStart=" + Uri.EscapeDataString(startUtc.Value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)));

        parts.Add("dateEnd=" + Uri.EscapeDataString(endUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)));
        var separator = snapshot.ApiUrl.Contains('?') ? "&" : "?";
        return snapshot.ApiUrl + separator + string.Join("&", parts);
    }

    private void ProcessOrder(ShopierRuntimeConfig snapshot, JsonElement order)
    {
        var orderId = ShopierApiParser.GetString(order, "id") ?? "";
        if (string.IsNullOrWhiteSpace(orderId)) return;
        var existingStatus = db.GetShopierOrderStatus(orderId);
        if (existingStatus is not null && (existingStatus.Equals("ACCEPTED", StringComparison.OrdinalIgnoreCase) || existingStatus.Equals("IGNORED_INITIAL_SYNC", StringComparison.OrdinalIgnoreCase)))
            return;

        var createdText = ShopierApiParser.GetString(order, "dateCreated") ?? "";
        var hasCreated = DateTimeOffset.TryParse(createdText, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var createdUtc);
        if (!snapshot.ProcessExistingOrdersOnFirstRun && (!hasCreated || createdUtc <= baselineUtc))
        {
            var payment = ShopierApiParser.GetString(order, "paymentStatus") ?? "";
            var noteText = ShopierApiParser.GetString(order, "note") ?? "";
            var oldSteam = Steam64.Match(noteText).Value;
            db.RecordShopierOrder(new ShopierOrderRecord(orderId, oldSteam, "", "", payment, noteText,
                "IGNORED_INITIAL_SYNC", "Yeni Raven veritabanında geçmiş Shopier siparişi otomatik lisans oluşturmasın diye atlandı.",
                null, hasCreated ? createdUtc : DateTimeOffset.UtcNow));
            return;
        }

        var configuredMatches = new List<(ShopierProductConfig Config, JsonElement LineItem, string ProductId, string Title)>();
        foreach (var product in snapshot.Products)
        {
            var candidate = ShopierApiParser.FindConfiguredProduct(order, product.ProductId, product.Title);
            if (candidate is null) continue;
            configuredMatches.Add((product, candidate.Value.LineItem, candidate.Value.ProductId, candidate.Value.Title));
        }
        if (configuredMatches.Count == 0) return;

        var paymentStatus = ShopierApiParser.GetString(order, "paymentStatus") ?? "";
        if (!paymentStatus.Equals("paid", StringComparison.OrdinalIgnoreCase)) return;

        var note = ShopierApiParser.GetString(order, "note") ?? "";
        var steamId = Steam64.Match(note).Value;
        if (configuredMatches.Count != 1)
        {
            db.RecordShopierOrder(new ShopierOrderRecord(orderId, steamId, "", "", paymentStatus, note,
                "REJECTED", "Siparişte birden fazla Raven Standard/Premium ürünü eşleşti. Tek siparişte yalnız bir Raven lisans ürünü olmalıdır.",
                null, hasCreated ? createdUtc : DateTimeOffset.UtcNow));
            return;
        }

        var selected = configuredMatches[0];
        var configured = selected.Config;
        var match = (LineItem: selected.LineItem, ProductId: selected.ProductId, Title: selected.Title);
        var currency = ShopierApiParser.GetString(order, "currency") ?? "";
        var quantity = ShopierApiParser.GetInt(match.LineItem, "quantity", 1);
        var actualPrice = ShopierApiParser.GetString(match.LineItem, "price") ?? "";
        var actualType = ShopierApiParser.GetString(match.LineItem, "type") ?? "";

        string? error = null;
        if (string.IsNullOrWhiteSpace(steamId)) error = "Sipariş notunda geçerli SteamID64 bulunamadı.";
        else if (quantity != 1) error = "Raven Map Panel sipariş adedi 1 olmalıdır.";
        else if (!string.IsNullOrWhiteSpace(actualType) && !actualType.Equals("digital", StringComparison.OrdinalIgnoreCase)) error = "Eşleşen Shopier ürünü dijital değil.";
        else if (!string.IsNullOrWhiteSpace(configured.ProductId) && !match.ProductId.Equals(configured.ProductId, StringComparison.OrdinalIgnoreCase)) error = "Shopier ürün ID eşleşmedi.";
        else if (!string.IsNullOrWhiteSpace(configured.ExpectedCurrency) && !currency.Equals(configured.ExpectedCurrency, StringComparison.OrdinalIgnoreCase)) error = "Para birimi eşleşmedi.";
        else if (!string.IsNullOrWhiteSpace(configured.ExpectedUnitPrice) && NormalizePrice(actualPrice) != NormalizePrice(configured.ExpectedUnitPrice)) error = "Birim fiyat eşleşmedi.";

        if (error is not null)
        {
            db.RecordShopierOrder(new ShopierOrderRecord(orderId, steamId, match.ProductId, match.Title,
                paymentStatus, note, "REJECTED", error, null, hasCreated ? createdUtc : DateTimeOffset.UtcNow));
            return;
        }

        var purchaseUtc = hasCreated ? createdUtc : DateTimeOffset.UtcNow;
        db.ApplyShopierOrder(new ParsedShopierOrder(orderId, steamId, match.ProductId, match.Title,
            paymentStatus, note, purchaseUtc), configured.LicenseDays, gracePeriod, configured.Plan);
    }

    private int GetDelaySeconds(ShopierRuntimeConfig snapshot)
    {
        if (consecutiveFailures <= 0) return snapshot.CheckIntervalSeconds;
        var exponent = Math.Min(4, Math.Max(0, consecutiveFailures - 1));
        var delay = snapshot.CheckIntervalSeconds * Math.Pow(2d, exponent);
        if (LastError.Contains("429", StringComparison.OrdinalIgnoreCase)) delay = Math.Max(delay, 120d);
        return (int)Math.Min(delay, snapshot.MaximumBackoffSeconds);
    }

    private static string NormalizePrice(string value)
    {
        if (decimal.TryParse((value ?? "").Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
            return d.ToString("0.00", CultureInfo.InvariantCulture);
        return (value ?? "").Trim();
    }

    private static string Trim(string value, int max) => string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max] + "…";

    private sealed class ShopierRateLimitException(string message, int retryAfterSeconds) : Exception(message)
    {
        public int RetryAfterSeconds { get; } = retryAfterSeconds;
    }
}
