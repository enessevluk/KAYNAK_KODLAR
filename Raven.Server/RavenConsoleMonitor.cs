using System.Collections.Concurrent;
using System.Diagnostics;

sealed class RavenConsoleMonitor
{
    private readonly DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
    private readonly ConcurrentDictionary<string, DateTimeOffset> recentClients = new(StringComparer.OrdinalIgnoreCase);
    private readonly object consoleLock = new();
    private readonly string logDirectory;
    private long totalRequests;
    private long failedRequests;
    private long slowRequests;
    private long activeRequests;
    private long totalElapsedMs;
    private TimeSpan previousCpu = Process.GetCurrentProcess().TotalProcessorTime;
    private DateTimeOffset previousCpuSample = DateTimeOffset.UtcNow;

    public RavenConsoleMonitor(string logDirectory)
    {
        this.logDirectory = logDirectory;
        try { Directory.CreateDirectory(logDirectory); } catch { }
    }

    public async Task HandleAsync(HttpContext ctx, Func<Task> next)
    {
        var sw = Stopwatch.StartNew();
        Interlocked.Increment(ref activeRequests);
        var path = ctx.Request.Path.Value ?? "/";
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
        var isApiClient = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
        if (isApiClient && !path.Equals("/api/shopier/status", StringComparison.OrdinalIgnoreCase))
            RegisterClient(ip, path);

        Exception? exception = null;
        try
        {
            await next();
        }
        catch (Exception ex)
        {
            exception = ex;
            throw;
        }
        finally
        {
            sw.Stop();
            Interlocked.Decrement(ref activeRequests);
            Interlocked.Increment(ref totalRequests);
            Interlocked.Add(ref totalElapsedMs, sw.ElapsedMilliseconds);

            var failed = exception is not null || ctx.Response.StatusCode >= 500;
            var slow = sw.ElapsedMilliseconds >= 1000;
            if (failed) Interlocked.Increment(ref failedRequests);
            if (slow) Interlocked.Increment(ref slowRequests);

            if (failed)
            {
                Write("HATA", $"{ctx.Request.Method} {path} -> {ctx.Response.StatusCode} · {sw.ElapsedMilliseconds} ms · {ip}", ConsoleColor.Red);
                if (exception is not null) Write("HATA", exception.GetType().Name + ": " + exception.Message, ConsoleColor.Red);
            }
            else if (slow)
            {
                Write("YAVAS", $"{ctx.Request.Method} {path} -> {ctx.Response.StatusCode} · {sw.ElapsedMilliseconds} ms · {ip}", ConsoleColor.Yellow);
            }
            else if (path.Equals("/api/access/activate", StringComparison.OrdinalIgnoreCase))
            {
                Write("LISANS", $"Aktivasyon istegi tamamlandi · HTTP {ctx.Response.StatusCode} · {ip}", ctx.Response.StatusCode < 400 ? ConsoleColor.Green : ConsoleColor.Yellow);
            }
            else if (path.StartsWith("/auth/steam/callback", StringComparison.OrdinalIgnoreCase))
            {
                Write("STEAM", $"Steam dogrulama callback · HTTP {ctx.Response.StatusCode} · {ip}", ctx.Response.StatusCode < 400 ? ConsoleColor.Green : ConsoleColor.Yellow);
            }
            else if (path.Equals("/admin/login", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsPost(ctx.Request.Method))
            {
                Write("ADMIN", $"Admin giris denemesi · HTTP {ctx.Response.StatusCode} · {ip}", ctx.Response.StatusCode < 400 ? ConsoleColor.Green : ConsoleColor.Yellow);
            }
        }
    }

    private void RegisterClient(string ip, string path)
    {
        var now = DateTimeOffset.UtcNow;
        var shouldLog = !recentClients.TryGetValue(ip, out var last) || now - last > TimeSpan.FromMinutes(10);
        recentClients[ip] = now;
        if (shouldLog)
            Write("CLIENT", $"Yeni/geri donen istemci: {ip} · {FriendlyPath(path)}", ConsoleColor.Cyan);
    }

    public async Task RunStatusLoopAsync(ServerRuntime runtime, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var pair in recentClients.ToArray())
                    if (now - pair.Value > TimeSpan.FromMinutes(10)) recentClients.TryRemove(pair.Key, out _);

                using var proc = Process.GetCurrentProcess();
                var currentCpu = proc.TotalProcessorTime;
                var elapsed = Math.Max(0.1, (now - previousCpuSample).TotalSeconds);
                var cpuDelta = Math.Max(0, (currentCpu - previousCpu).TotalSeconds);
                var cpu = Math.Clamp(cpuDelta / elapsed / Math.Max(1, Environment.ProcessorCount) * 100.0, 0, 100);
                previousCpu = currentCpu;
                previousCpuSample = now;

                var total = Interlocked.Read(ref totalRequests);
                var totalMs = Interlocked.Read(ref totalElapsedMs);
                var avg = total == 0 ? 0 : totalMs / (double)total;
                var shopier = runtime.ShopierEnabled
                    ? (string.IsNullOrWhiteSpace(runtime.ShopierPoller.LastError) ? "OK" : "HATA")
                    : "KAPALI";
                var color = Interlocked.Read(ref failedRequests) == 0 ? ConsoleColor.DarkGreen : ConsoleColor.Yellow;
                Write("DURUM", $"Uptime {FormatUptime(now - startedUtc)} · Aktif client(10dk) {recentClients.Count} · Istek {total} · Aktif {Interlocked.Read(ref activeRequests)} · Hata {Interlocked.Read(ref failedRequests)} · Yavas {Interlocked.Read(ref slowRequests)} · Ort {avg:0} ms · CPU {cpu:0.0}% · RAM {proc.WorkingSet64 / 1024d / 1024d:0} MB · Shopier {shopier}", color);
                if (runtime.ShopierEnabled && !string.IsNullOrWhiteSpace(runtime.ShopierPoller.LastError))
                    Write("SHOPIER", runtime.ShopierPoller.LastError, ConsoleColor.Yellow);
            }
        }
        catch (OperationCanceledException) { }
    }

    public void PrintReady(string publicBaseUrl)
    {
        Write("HAZIR", $"Raven Server baglanti kabul ediyor · {publicBaseUrl}", ConsoleColor.Green);
    }

    public void PrintStopping()
    {
        Write("DURUM", "Raven Server kapatiliyor...", ConsoleColor.Yellow);
    }

    public void PrintBanner(string version, string buildId, string listenUrl, string publicBaseUrl, bool development, bool shopierEnabled)
    {
        lock (consoleLock)
        {
            // Raven.Server normal CMD, redirected preflight, scheduled task veya servis
            // olarak calisabilir. Console.Clear/renk API'leri gercek konsol handle'i
            // olmadiginda Windows'ta IOException atabilir. Server bunun yuzunden
            // asla kapanmamali.
            var interactive = CanUseInteractiveConsole();
            if (interactive)
            {
                try { Console.Clear(); } catch (IOException) { interactive = false; } catch (PlatformNotSupportedException) { interactive = false; }
            }

            WriteConsoleLine("====================================================================", ConsoleColor.DarkGray, interactive);
            WriteConsoleLine("                       RAVEN SERVER", ConsoleColor.Cyan, interactive);
            WriteConsoleLine("====================================================================", ConsoleColor.DarkGray, interactive);
            Console.WriteLine($"  Surum       : v{version}");
            Console.WriteLine($"  Build       : {buildId}");
            Console.WriteLine($"  Ortam       : {(development ? "Development" : "Production")}");
            Console.WriteLine($"  Dinleme     : {listenUrl}");
            Console.WriteLine($"  Dis adres   : {publicBaseUrl}");
            Console.WriteLine($"  Shopier     : {(shopierEnabled ? "AKTIF" : "KAPALI")}");
            Console.WriteLine($"  Baslangic   : {DateTimeOffset.Now:dd.MM.yyyy HH:mm:ss}");
            Console.WriteLine($"  Log         : {Path.Combine(logDirectory, "raven-server-YYYYMMDD.log")}");
            WriteConsoleLine("--------------------------------------------------------------------", ConsoleColor.DarkGray, interactive);
            Console.WriteLine("  Durum ozeti her 60 saniyede bir yazilir. 1 sn uzeri istek YAVAS.");
            WriteConsoleLine("====================================================================", ConsoleColor.DarkGray, interactive);
        }
    }

    private void Write(string tag, string message, ConsoleColor color)
    {
        lock (consoleLock)
        {
            var now = DateTimeOffset.Now;
            var interactive = CanUseInteractiveConsole();

            if (!interactive)
            {
                // Redirect edilen stdout (preflight/servis) icin sadece duz metin.
                Console.WriteLine($"[{now:HH:mm:ss}] [{tag}] {message}");
            }
            else
            {
                try
                {
                    var old = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write($"[{now:HH:mm:ss}] ");
                    Console.ForegroundColor = color;
                    Console.Write($"[{tag}] ");
                    Console.ForegroundColor = old;
                    Console.WriteLine(message);
                }
                catch (IOException)
                {
                    Console.WriteLine($"[{now:HH:mm:ss}] [{tag}] {message}");
                }
                catch (PlatformNotSupportedException)
                {
                    Console.WriteLine($"[{now:HH:mm:ss}] [{tag}] {message}");
                }
            }

            try
            {
                Directory.CreateDirectory(logDirectory);
                var file = Path.Combine(logDirectory, $"raven-server-{now:yyyyMMdd}.log");
                File.AppendAllText(file, $"[{now:yyyy-MM-dd HH:mm:ss zzz}] [{tag}] {message}{Environment.NewLine}");
            }
            catch { }
        }
    }

    private static bool CanUseInteractiveConsole()
    {
        try
        {
            if (string.Equals(Environment.GetEnvironmentVariable("RAVEN_NONINTERACTIVE"), "1", StringComparison.Ordinal))
                return false;

            return Environment.UserInteractive && !Console.IsOutputRedirected && !Console.IsErrorRedirected;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteConsoleLine(string text, ConsoleColor color, bool useColor)
    {
        if (!useColor)
        {
            Console.WriteLine(text);
            return;
        }

        try
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ForegroundColor = old;
        }
        catch (IOException)
        {
            Console.WriteLine(text);
        }
        catch (PlatformNotSupportedException)
        {
            Console.WriteLine(text);
        }
    }

    private static string FriendlyPath(string path) => path switch
    {
        "/api/bootstrap" => "client baslangic bilgisi",
        "/api/license/validate" => "lisans dogrulama",
        "/api/auth/session" => "Steam giris oturumu",
        "/api/access/activate" => "lisans aktivasyonu",
        _ => path
    };

    private static string FormatUptime(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}g {t.Hours:00}s {t.Minutes:00}d";
        return $"{(int)t.TotalHours:00}s {t.Minutes:00}d {t.Seconds:00}sn";
    }
}
