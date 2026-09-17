namespace RavenMapPanel;

internal enum GenerationQueueStage { Starting, Completed }

internal sealed record GenerationQueueProgress(
    GenerationQueueStage Stage,
    int Number,
    int Count,
    int Seed,
    GenerationResult? Result = null);

internal sealed record GenerationQueueResult(
    IReadOnlyList<int> Seeds,
    IReadOnlyList<GenerationResult> Results);

internal sealed class GenerationQueueService(IReadOnlyList<MonumentRule> catalog, ProjectService projectService, LicenseService licenseService)
{
    public event Action<string>? Log;

    public async Task<GenerationQueueResult> GenerateAsync(
        AppSettings template,
        CancellationToken cancellation,
        Action<GenerationQueueProgress>? progress = null)
    {
        var queueSettings = projectService.Clone(template);
        var count = Math.Clamp(queueSettings.GenerationCount, 1, 7);
        var seeds = BuildSeeds(queueSettings.Seed, count);
        var results = new List<GenerationResult>(count);

        for (var index = 0; index < seeds.Count; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            queueSettings.Seed = seeds[index];
            var number = index + 1;
            progress?.Invoke(new GenerationQueueProgress(GenerationQueueStage.Starting, number, count, queueSettings.Seed));
            Log?.Invoke($"KUYRUK: Harita {number}/{count} başlatıldı — Seed {queueSettings.Seed}");

            var service = new GenerationService(catalog.ToList(),licenseService);
            service.Log += message => Log?.Invoke(message);
            var result = await service.GenerateAsync(queueSettings, cancellation);
            results.Add(result);

            progress?.Invoke(new GenerationQueueProgress(GenerationQueueStage.Completed, number, count, queueSettings.Seed, result));
            Log?.Invoke($"KUYRUK: Harita {number}/{count} tamamlandı — {result.OutputFolder}");
        }

        return new GenerationQueueResult(seeds, results);
    }

    private static List<int> BuildSeeds(int firstSeed, int count)
    {
        var seeds = new List<int>(count) { firstSeed };
        var used = new HashSet<int> { firstSeed };
        while (seeds.Count < count)
        {
            var seed = Random.Shared.Next(1, int.MaxValue);
            if (used.Add(seed)) seeds.Add(seed);
        }
        return seeds;
    }
}
