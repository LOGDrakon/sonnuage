using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace SonNuage.Server.Analysis;

/// <summary>
/// Analyse par le script Python d'Essentia (<c>Analysis/essentia_analyzer.py</c>), menée par plusieurs processus côte à
/// côte (<see cref="AnalysisOptions.EffectiveWorkers"/>), un fichier chacun : le décodage et le tempo n'occupent qu'un
/// cœur, plusieurs processus en occupent plusieurs. Chaque processus est lancé à la première demande qu'il reçoit et
/// gardé en vie tant qu'il a du travail (<see cref="EssentiaProcess"/>).
/// </summary>
public sealed class EssentiaAudioAnalyzer : IAudioAnalyzer, IDisposable
{
    private const string BundledScript = "Analysis/essentia_analyzer.py";
    /// <summary>Plantages d'affilée au-delà desquels l'analyseur est jugé hors d'usage (et plus aucun morceau marqué en échec).</summary>
    private const int MaxConsecutiveCrashes = 3;

    private readonly AnalysisOptions _options;
    private readonly string _script;
    private readonly ILogger<EssentiaAudioAnalyzer> _logger;
    private readonly Lazy<bool> _available;
    private readonly SemaphoreSlim _slots;
    /// <summary>Processus libres ; un processus n'analyse qu'un fichier à la fois.</summary>
    private readonly ConcurrentBag<EssentiaProcess> _free = [];
    private readonly List<EssentiaProcess> _all = [];
    private int _crashes;

    public EssentiaAudioAnalyzer(IOptions<SonNuageOptions> options, ILogger<EssentiaAudioAnalyzer> logger)
    {
        _options = options.Value.Analysis;
        _script = string.IsNullOrWhiteSpace(_options.ScriptPath) ? Path.Combine(AppContext.BaseDirectory, BundledScript) : _options.ScriptPath;
        _logger = logger;
        Parallelism = _options.EffectiveWorkers;
        _slots = new SemaphoreSlim(Parallelism, Parallelism);
        _available = new Lazy<bool>(() =>
        {
            var ok = File.Exists(_options.PythonPath) && Directory.Exists(_options.ModelsPath) && File.Exists(_script);
            if (ok) logger.LogInformation("Analyse audio disponible : {Python} {Script}, {Workers} processus", _options.PythonPath, _script, Parallelism);
            else logger.LogInformation("Analyse audio indisponible (Python, modèles ou script absents)");
            return ok;
        });
    }

    public bool Available => _available.Value;

    public int Parallelism { get; }

    public async Task<AudioAnalysisOutcome> AnalyzeAsync(string path, CancellationToken ct)
    {
        if (!Available) throw new AudioAnalyzerUnavailableException("Essentia analyzer is not installed.");
        await _slots.WaitAsync(ct);
        var worker = Take();
        try
        {
            var result = await worker.AnalyzeAsync(path, ct);
            if (result is not EssentiaProcess.Crashed crashed)
            {
                Interlocked.Exchange(ref _crashes, 0);
                return ((EssentiaProcess.Done)result).Outcome;
            }
            if (Interlocked.Increment(ref _crashes) >= MaxConsecutiveCrashes)
                throw new AudioAnalyzerUnavailableException($"Essentia analyzer keeps stopping (exit code {crashed.ExitCode}).");
            return new AudioAnalysisOutcome.Failed($"Analyzer stopped on this file (exit code {crashed.ExitCode})");
        }
        finally
        {
            _free.Add(worker);
            _slots.Release();
        }
    }

    /// <summary>Un processus libre, ou un nouveau tant qu'il y en a moins que de places.</summary>
    private EssentiaProcess Take()
    {
        if (_free.TryTake(out var worker)) return worker;
        worker = new EssentiaProcess(_options, _script, _logger);
        lock (_all) _all.Add(worker);
        return worker;
    }

    public void Dispose()
    {
        lock (_all)
        {
            foreach (var worker in _all) worker.Dispose();
            _all.Clear();
        }
        _slots.Dispose();
    }
}
