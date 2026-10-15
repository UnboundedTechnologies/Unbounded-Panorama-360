using System.Diagnostics;
using Unbounded.Panorama.Layout;
using Unbounded.Panorama.Media;
using Unbounded.Panorama.Session;
using Unbounded.Panorama.Transport;
using Unbounded.Panorama.Wire;

namespace Unbounded.Panorama.Engine;

public enum EngineState
{
    Starting,
    NoDevice,
    BlockedByKanali,
    Connecting,
    Connected,
}

/// <summary>
/// Owns the cooler: connects (and reconnects), applies layouts, keeps live values flowing.
/// </summary>
public sealed class DisplayEngine : IAsyncDisposable
{
    public const string RemotePrefix = "upn360-";

    private readonly MediaPreparer _preparer;
    private readonly WidgetText _text;
    private readonly SemaphoreSlim _apply = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _loop;

    private PanoramaSession? _session;
    private ScreenLayout? _applied;
    private string? _appliedMediaName;
    private int _metricFailures;

    public event Action<EngineState, string>? StateChanged;
    public event Action<string>? Log;

    public EngineState State { get; private set; } = EngineState.Starting;
    public string StateDetail { get; private set; } = "";
    public DeviceInformation? Device => _session?.Information;

    public DisplayEngine(MediaPreparer preparer, WidgetText text)
    {
        _preparer = preparer;
        _text = text;
        _loop = Task.Run(RunAsync);
    }

    // ------------------------------------------------------------------ KANALI

    public static Process[] KanaliProcesses()
    {
        var list = new List<Process>(Process.GetProcessesByName("KANALI"));
        foreach (var p in Process.GetProcessesByName("Screen"))
        {
            try
            {
                if (p.MainModule?.FileName?.Contains(@"\KANALI\", StringComparison.OrdinalIgnoreCase) == true)
                    list.Add(p);
            }
            catch
            {
                // Path not readable: not ours to judge, leave it alone.
            }
        }
        return [.. list];
    }

    public static void CloseKanali()
    {
        foreach (var p in KanaliProcesses())
        {
            try
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
            catch
            {
            }
        }
    }

    // ------------------------------------------------------------------ apply

    /// <summary>
    /// Sends a layout to the cooler. With <paramref name="includeBackground"/> the background is
    /// converted (cached), uploaded if the cooler does not have it yet, and set as the playing media.
    /// </summary>
    public async Task ApplyAsync(ScreenLayout layout, bool includeBackground, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var snapshot = layout.Clone();
        await _apply.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = _session ?? throw new InvalidOperationException("The cooler is not connected.");
            string? mediaName = null;
            if (includeBackground)
            {
                // No background chosen means a black screen, not whatever was playing before.
                var prepared = snapshot.Background.SourcePath is null
                    ? await _preparer.PrepareBlackAsync(cancellationToken).ConfigureAwait(false)
                    : await _preparer.PrepareAsync(snapshot.Background, snapshot.Orientation, progress, cancellationToken).ConfigureAwait(false);
                var catalog = await session.GetMediaCatalogAsync(cancellationToken).ConfigureAwait(false);
                var existing = catalog.MediaFileList.FirstOrDefault(e => PanoramaSession.MediaName(e) == prepared.RemoteName);
                if (existing is null || existing.FileSize != prepared.Size)
                {
                    progress?.Report($"Uploading background ({prepared.Size / 1048576.0:F1} MB)…");
                    var bytes = await File.ReadAllBytesAsync(prepared.StreamPath, cancellationToken).ConfigureAwait(false);
                    await session.UploadMediaAsync(prepared.RemoteName, bytes,
                        new Progress<double>(p => progress?.Report($"Uploading background… {p:P0}")), cancellationToken).ConfigureAwait(false);
                }
                mediaName = prepared.RemoteName;
            }

            progress?.Report("Applying to the cooler…");
            await session.SetOverlayAsync(LayoutCompiler.Compile(snapshot, w => _text.Resolve(w, snapshot.Fahrenheit)), cancellationToken).ConfigureAwait(false);
            var (_, after) = await session.UpdateUserConfigurationAsync(c =>
            {
                c.DisplayConfig ??= new DisplayConfiguration();
                c.DisplayConfig.UiRotation = LayoutCompiler.UiRotation(snapshot.Orientation);
                c.DisplayConfig.MediaRotation = 0;
                c.DisplayConfig.Mirror = false;
                c.DisplayConfig.BacklightEnable = true;
                c.DisplayConfig.BacklightBrightness = (uint)Math.Clamp(snapshot.Brightness, 0, 100);
                if (mediaName is not null)
                {
                    c.WorkConfig ??= new WorkConfiguration();
                    c.WorkConfig.MediaMode = WorkConfiguration.Types.MediaMode.MediaSingle;
                    c.WorkConfig.LoopMode = WorkConfiguration.Types.LoopMode.LoopSingle;
                    c.WorkConfig.SingleModeMediaFile = mediaName;
                }
            }, cancellationToken).ConfigureAwait(false);

            if (after.DisplayConfig?.UiRotation != LayoutCompiler.UiRotation(snapshot.Orientation)
                || (mediaName is not null && after.WorkConfig?.SingleModeMediaFile != mediaName))
                throw new DeviceProtocolException("The cooler did not keep the requested settings (readback differs).");

            _applied = snapshot;
            if (mediaName is not null)
            {
                _appliedMediaName = mediaName;
                await RemoveStaleUploadsAsync(session, after, cancellationToken).ConfigureAwait(false);
            }
            progress?.Report("Applied.");
            Log?.Invoke($"applied layout: {snapshot.Widgets.Count} widget(s), {snapshot.Orientation}{(mediaName is null ? "" : $", background {mediaName}")}");
        }
        finally
        {
            _apply.Release();
        }
    }

    /// <summary>Quick overlay-only update for live editing (no config write, no upload).</summary>
    public async Task PushOverlayAsync(ScreenLayout layout, CancellationToken cancellationToken = default)
    {
        var snapshot = layout.Clone();
        if (!await _apply.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return; // a full apply is running; it will send the overlay itself
        try
        {
            var session = _session;
            if (session is null || _applied is null || _applied.Orientation != snapshot.Orientation)
                return; // orientation changes need a full apply
            await session.SetOverlayAsync(LayoutCompiler.Compile(snapshot, w => _text.Resolve(w, snapshot.Fahrenheit)), cancellationToken).ConfigureAwait(false);
            _applied = snapshot;
        }
        finally
        {
            _apply.Release();
        }
    }

    /// <summary>Deletes our own earlier backgrounds that nothing on the cooler uses any more.</summary>
    private async Task RemoveStaleUploadsAsync(PanoramaSession session, UserConfiguration config, CancellationToken cancellationToken)
    {
        var inUse = new HashSet<string>(StringComparer.Ordinal)
        {
            Bare(config.WorkConfig?.SingleModeMediaFile),
            Bare(config.WorkConfig?.DualModeLeftMediaFile),
            Bare(config.WorkConfig?.DualModeRightMediaFile),
            Bare(config.PoweronConfig?.MediaFile),
            Bare(config.StandbyConfig?.MediaFile),
        };
        var catalog = await session.GetMediaCatalogAsync(cancellationToken).ConfigureAwait(false);
        foreach (var entry in catalog.MediaFileList)
        {
            var name = PanoramaSession.MediaName(entry);
            if (!name.StartsWith(RemotePrefix, StringComparison.Ordinal) || inUse.Contains(name) || entry.ReadOnly)
                continue;
            try
            {
                await session.RemoveMediaAsync(name, cancellationToken).ConfigureAwait(false);
                Log?.Invoke($"removed old background {name}");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"could not remove {name}: {ex.Message}");
            }
        }
    }

    private static string Bare(string? path) => path is null ? "" : path[(path.LastIndexOf('/') + 1)..];

    // ------------------------------------------------------------------ loop

    private async Task RunAsync()
    {
        var token = _shutdown.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_session is null)
                    await TryConnectAsync(token).ConfigureAwait(false);
                else
                    await TickAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"engine: {ex.Message}");
            }

            try
            {
                await Task.Delay(_session is null ? 2000 : 1000, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task TryConnectAsync(CancellationToken token)
    {
        if (KanaliProcesses().Length > 0)
        {
            SetState(EngineState.BlockedByKanali, "KANALI is running and owns the cooler.");
            return;
        }
        var devices = DeviceDiscovery.Enumerate();
        if (devices.Count == 0)
        {
            SetState(EngineState.NoDevice, "No TRYX Panorama found on USB.");
            return;
        }

        SetState(EngineState.Connecting, $"Connecting to {devices[0].ProductName}…");
        try
        {
            // Per-frame traffic (pings every 2 s, values every 1 s) would drown the activity log;
            // only problems and milestones are forwarded.
            _session = await PanoramaSession.ConnectAsync(devices[0], m =>
            {
                if (m.Contains("fail", StringComparison.OrdinalIgnoreCase) || m.Contains("ERROR", StringComparison.Ordinal)
                    || m.Contains("unparseable", StringComparison.Ordinal) || m.StartsWith("bootstrap", StringComparison.Ordinal))
                    Log?.Invoke(m);
            }, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetState(EngineState.NoDevice, $"Could not connect: {ex.Message}");
            return;
        }
        _metricFailures = 0;
        var info = _session.Information;
        SetState(EngineState.Connected, $"{devices[0].ProductName} · firmware {info?.FirmwareVersion}");

        // After a reconnect, put the last applied overlay back (configuration persists on the device).
        if (_applied is { } applied)
        {
            try
            {
                await _session.SetOverlayAsync(LayoutCompiler.Compile(applied, w => _text.Resolve(w, applied.Fahrenheit)), token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log?.Invoke($"overlay restore failed: {ex.Message}");
            }
        }
    }

    private async Task TickAsync(CancellationToken token)
    {
        var session = _session!;
        var applied = _applied;
        if (applied is null || !applied.Widgets.Any(w => LayoutCompiler.IsDynamic(w.Content)))
            return;
        if (!await _apply.WaitAsync(0, token).ConfigureAwait(false))
            return; // skip live values while an apply is running
        try
        {
            var batch = LayoutCompiler.CompileUpdates(applied, w => _text.Resolve(w, applied.Fahrenheit));
            await session.SendMetricsAsync(batch, token).ConfigureAwait(false);
            _metricFailures = 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _metricFailures++;
            Log?.Invoke($"live update failed ({_metricFailures}): {ex.Message}");
            if (_metricFailures >= 3)
                await DropSessionAsync("Lost the cooler; reconnecting…").ConfigureAwait(false);
        }
        finally
        {
            _apply.Release();
        }
    }

    private async Task DropSessionAsync(string reason)
    {
        var session = _session;
        _session = null;
        SetState(EngineState.NoDevice, reason);
        if (session is not null)
            await session.DisposeAsync().ConfigureAwait(false);
    }

    private void SetState(EngineState state, string detail)
    {
        if (State == state && StateDetail == detail)
            return;
        State = state;
        StateDetail = detail;
        StateChanged?.Invoke(state, detail);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        if (_session is not null)
            await _session.DisposeAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }
}
