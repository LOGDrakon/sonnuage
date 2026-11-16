using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using SonNuage.Server.Data;

namespace SonNuage.Server.Analysis;

/// <summary>
/// Un processus du script d'analyse : lancé à la première demande, gardé en vie tant qu'il a du travail (ses modèles ne
/// sont chargés qu'au démarrage), une demande à la fois. Il s'arrête après <see cref="AnalysisOptions.IdleMinutes"/> sans
/// fichier, ou est relancé quand un fichier le bloque ou le tue.
/// </summary>
internal sealed class EssentiaProcess(AnalysisOptions options, string script, ILogger logger) : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Issue d'une demande : un résultat du script (analyse ou échec sur ce fichier), ou un processus mort en route.</summary>
    public abstract record Result;
    public sealed record Done(AudioAnalysisOutcome Outcome) : Result;
    public sealed record Crashed(string ExitCode) : Result;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Timer? _idle;
    private Process? _process;

    public async Task<Result> AnalyzeAsync(string path, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _idle?.Change(Timeout.Infinite, Timeout.Infinite);
            var process = _process ?? await StartAsync(ct);
            var id = Guid.NewGuid().ToString("N");
            await process.StandardInput.WriteLineAsync(EssentiaProtocol.Request(id, path).AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)));
            try
            {
                while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
                {
                    if (EssentiaProtocol.Parse(line) is EssentiaProtocol.Result r && r.Id == id) return new Done(r.Outcome);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Stop();
                return new Done(new AudioAnalysisOutcome.Failed($"Timeout: no result after {options.TimeoutSeconds} s"));
            }
            catch (OperationCanceledException)
            {
                // Tâche annulée : le processus est occupé par ce fichier, il repartira à neuf.
                Stop();
                throw;
            }

            var code = process.HasExited ? process.ExitCode.ToString(CultureInfo.InvariantCulture) : "?";
            Stop();
            return new Crashed(code);
        }
        finally
        {
            if (_process is not null)
            {
                _idle ??= new Timer(_ => StopWhenIdle());
                _idle.Change(TimeSpan.FromMinutes(Math.Max(1, options.IdleMinutes)), Timeout.InfiniteTimeSpan);
            }
            _gate.Release();
        }
    }

    private async Task<Process> StartAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo(options.PythonPath!)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "-I", script, "--models", options.ModelsPath!, "--threads", Arg(options.Threads), "--nice", Arg(options.Niceness) })
            psi.ArgumentList.Add(arg);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new AudioAnalyzerUnavailableException("Essentia analyzer did not start.");
        }
        catch (Win32Exception ex)
        {
            throw new AudioAnalyzerUnavailableException($"Essentia analyzer did not start: {ex.Message}");
        }
        // Journaux de TensorFlow et d'Essentia : lus en continu (un tuyau plein bloquerait le script).
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) logger.LogDebug("Essentia : {Line}", e.Data);
        };
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(StartTimeout);
        try
        {
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                switch (EssentiaProtocol.Parse(line))
                {
                    case EssentiaProtocol.Ready { Version: TrackAudioFeatures.CurrentVersion }:
                        logger.LogInformation("Analyse audio : modèles chargés (processus {Pid})", process.Id);
                        _process = process;
                        return process;
                    case EssentiaProtocol.Ready ready:
                        throw new AudioAnalyzerUnavailableException(
                            $"Essentia analyzer computes version {ready.Version}, the server expects {TrackAudioFeatures.CurrentVersion}.");
                    case EssentiaProtocol.Fatal fatal:
                        throw new AudioAnalyzerUnavailableException($"Essentia analyzer failed to start: {fatal.Error}");
                }
            }
            throw new AudioAnalyzerUnavailableException("Essentia analyzer exited while loading its models.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Kill(process);
            throw new AudioAnalyzerUnavailableException("Essentia analyzer took too long to load its models.");
        }
        catch
        {
            Kill(process);
            throw;
        }
    }

    private static string Arg(int value) => value.ToString(CultureInfo.InvariantCulture);

    private void StopWhenIdle()
    {
        if (!_gate.Wait(0)) return;
        try
        {
            if (_process is not null) logger.LogInformation("Analyse audio : processus arrêté (inactif)");
            Stop();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Stop()
    {
        if (_process is null) return;
        Kill(_process);
        _process = null;
    }

    private static void Kill(Process process)
    {
        try
        {
            // Fin de l'entrée : le script sort de lui-même ; sinon, arrêt forcé.
            process.StandardInput.Close();
            if (!process.WaitForExit(2000)) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException)
        {
        }
        process.Dispose();
    }

    public void Dispose()
    {
        _idle?.Dispose();
        Stop();
        _gate.Dispose();
    }
}
