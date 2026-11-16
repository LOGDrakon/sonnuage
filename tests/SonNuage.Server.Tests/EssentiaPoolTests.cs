using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SonNuage.Server.Analysis;
using SonNuage.Server.Data;

namespace SonNuage.Server.Tests;

/// <summary>
/// Les processus d'analyse menés côte à côte, avec un faux script qui parle le protocole d'essentia_analyzer.py :
/// chaque fichier prend <see cref="FileSeconds"/> s, un fichier « crash » tue son processus. Il faut Python.
/// </summary>
public sealed class EssentiaPoolTests : IDisposable
{
    private const double FileSeconds = 0.5;

    private const string FakeScript = """
        import json, os, sys, time
        models = sys.argv[sys.argv.index("--models") + 1]
        with open(os.path.join(models, "version.txt")) as f:
            version = int(f.read())
        print(json.dumps({"ready": True, "version": version}), flush=True)
        for line in sys.stdin:
            request = json.loads(line)
            if "crash" in request["path"]:
                sys.exit(3)
            time.sleep(float(os.environ.get("FAKE_SECONDS", "0.5")))
            print(json.dumps({"id": request["id"], "features": {"happy": 0.5}}), flush=True)
        """;

    private readonly string _dir = Directory.CreateTempSubdirectory("sonnuage-essentia-").FullName;

    private EssentiaAudioAnalyzer Pool(int workers)
    {
        var python = FindPython();
        Assert.SkipUnless(python is not null, "Python absent");
        var models = Directory.CreateDirectory(Path.Combine(_dir, "models")).FullName;
        File.WriteAllText(Path.Combine(models, "version.txt"), TrackAudioFeatures.CurrentVersion.ToString());
        var script = Path.Combine(_dir, "fake_analyzer.py");
        File.WriteAllText(script, FakeScript);
        var options = new SonNuageOptions
        {
            Analysis = new AnalysisOptions { PythonPath = python, ModelsPath = models, ScriptPath = script, Workers = workers, TimeoutSeconds = 20 },
        };
        return new EssentiaAudioAnalyzer(Options.Create(options), NullLogger<EssentiaAudioAnalyzer>.Instance);
    }

    [Fact]
    public async Task Each_process_analyzes_its_own_file_at_the_same_time()
    {
        using var pool = Pool(workers: 3);
        var ct = TestContext.Current.CancellationToken;
        // Processus lancés et modèles « chargés » avant de mesurer.
        await Task.WhenAll(Enumerable.Range(0, 3).Select(i => pool.AnalyzeAsync($"warm-{i}.wav", ct)));

        var watch = Stopwatch.StartNew();
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => pool.AnalyzeAsync($"song-{i}.wav", ct)));
        watch.Stop();

        Assert.Equal(3, pool.Parallelism);
        Assert.All(outcomes, o => Assert.IsType<AudioAnalysisOutcome.Analyzed>(o));
        // Six fichiers sur trois processus : deux tours, pas six.
        Assert.InRange(watch.Elapsed.TotalSeconds, 2 * FileSeconds, 4.5 * FileSeconds);
    }

    [Fact]
    public async Task A_file_that_kills_its_process_fails_alone()
    {
        using var pool = Pool(workers: 2);
        var ct = TestContext.Current.CancellationToken;

        var outcomes = await Task.WhenAll(new[] { "a.wav", "crash.wav", "b.wav", "c.wav" }.Select(f => pool.AnalyzeAsync(f, ct)));

        Assert.IsType<AudioAnalysisOutcome.Failed>(outcomes[1]);
        Assert.All(outcomes.Where((_, i) => i != 1), o => Assert.IsType<AudioAnalysisOutcome.Analyzed>(o));
        // Le processus tué est relancé pour la suite.
        Assert.IsType<AudioAnalysisOutcome.Analyzed>(await pool.AnalyzeAsync("d.wav", ct));
    }

    /// <summary>Un vrai Python (pas le raccourci du Microsoft Store, qui ouvre le magasin au lieu de lancer Python).</summary>
    private static string? FindPython()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "python.exe", "python3.exe" } : ["python3", "python"];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (dir.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var name in names)
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path)) return path;
            }
        }
        return null;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
