using System.Diagnostics;
using Google.Protobuf;
using Unbounded.Panorama.Protocol;
using Unbounded.Panorama.Transport;
using Unbounded.Panorama.Wire;

namespace Unbounded.Panorama.Session;

public sealed class DeviceProtocolException(string message) : Exception(message);

/// <summary>
/// One device session: bootstrap, request/response matching, keepalive with overlay lease.
/// All device traffic is serialized through a single gate.
/// </summary>
public sealed class PanoramaSession : IAsyncDisposable
{
    public static readonly TimeSpan TransactionTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan OptionalResponseWindow = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(2);

    private readonly UsbPrintTransport _transport;
    private readonly Requests _requests = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Stopwatch _sinceLastWrite = Stopwatch.StartNew();
    private Task? _keepaliveLoop;
    private bool _leaseTurn;

    public event Action<string>? Log;

    public PanoramaDeviceInfo Device { get; }
    public DeviceInformation? Information { get; private set; }
    public SystemConfiguration? SystemConfiguration { get; private set; }

    /// <summary>The overlay currently shown; re-sent as a lease on alternate keepalive ticks.</summary>
    public OverlayLayout CurrentOverlay { get; private set; } = new();

    private PanoramaSession(PanoramaDeviceInfo device, UsbPrintTransport transport)
    {
        Device = device;
        _transport = transport;
        _transport.Log += m => Log?.Invoke($"transport: {m}");
    }

    public static async Task<PanoramaSession> ConnectAsync(PanoramaDeviceInfo device, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        var session = new PanoramaSession(device, UsbPrintTransport.Open(device.InterfacePath));
        if (log is not null)
            session.Log += log;
        try
        {
            await session.BootstrapAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        session._keepaliveLoop = Task.Run(session.KeepaliveLoopAsync);
        return session;
    }

    private async Task BootstrapAsync(CancellationToken cancellationToken)
    {
        var readiness = Stopwatch.StartNew();
        var backoffs = new[] { 500, 1000, 2000 };
        for (var attempt = 0; ; attempt++)
        {
            var response = await ExchangeAsync(Requests.DeviceInformationQuery(),
                r => r.BodyCase == Response.BodyOneofCase.DeviceInformation, TransactionTimeout, cancellationToken).ConfigureAwait(false);
            if (response is not null)
            {
                Information = response.DeviceInformation;
                break;
            }
            if (readiness.Elapsed > TimeSpan.FromSeconds(20))
                throw new DeviceProtocolException("Device did not answer DeviceInfo within 20 s.");
            await Task.Delay(backoffs[Math.Min(attempt, backoffs.Length - 1)], cancellationToken).ConfigureAwait(false);
        }

        SystemConfiguration = (await RequireAsync(Requests.SystemConfigurationQuery(), Response.BodyOneofCase.SystemConfiguration, cancellationToken).ConfigureAwait(false)).SystemConfiguration;
        await RequireAsync(Requests.DeviceAuthenticationQuery(), Response.BodyOneofCase.DeviceAuthentication, cancellationToken).ConfigureAwait(false);
        await RequireAsync(Requests.Keepalive(), Response.BodyOneofCase.Pong, cancellationToken).ConfigureAwait(false);
        Log?.Invoke($"bootstrap complete: {Information}");
    }

    // ------------------------------------------------------------------ queries

    public async Task<UserConfiguration> GetUserConfigurationAsync(CancellationToken cancellationToken = default)
    {
        var request = _requests.UserConfigurationQuery();
        return (await RequireAsync(request, Response.BodyOneofCase.UserConfiguration, cancellationToken).ConfigureAwait(false)).UserConfiguration;
    }

    public async Task<MediaCatalog> GetMediaCatalogAsync(CancellationToken cancellationToken = default)
    {
        var request = _requests.MediaCatalogQuery();
        return (await RequireAsync(request, Response.BodyOneofCase.MediaCatalog, cancellationToken).ConfigureAwait(false)).MediaCatalog;
    }

    // ------------------------------------------------------------------ mutations

    /// <summary>
    /// Writes a complete UserConfiguration (expects Acknowledgement), activates it with the
    /// current overlay, and returns the device readback.
    /// </summary>
    public async Task<UserConfiguration> WriteUserConfigurationAsync(UserConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var write = _requests.Tracked(r => r.UserConfiguration = configuration.Clone());
        await RequireAsync(write, Response.BodyOneofCase.Acknowledgement, cancellationToken).ConfigureAwait(false);
        await SendOverlayTrackedAsync(CurrentOverlay, cancellationToken).ConfigureAwait(false);
        return await GetUserConfigurationAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Read-modify-write: unknown fields from the device are preserved.</summary>
    public async Task<(UserConfiguration Before, UserConfiguration After)> UpdateUserConfigurationAsync(
        Action<UserConfiguration> modify, CancellationToken cancellationToken = default)
    {
        var before = await GetUserConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var next = before.Clone();
        modify(next);
        var after = await WriteUserConfigurationAsync(next, cancellationToken).ConfigureAwait(false);
        return (before, after);
    }

    public async Task SetOverlayAsync(OverlayLayout layout, CancellationToken cancellationToken = default)
    {
        await SendOverlayTrackedAsync(layout, cancellationToken).ConfigureAwait(false);
        CurrentOverlay = layout.Clone();
    }

    /// <summary>MetricBatch: no header, write-only; an explicit error in the optional reply is fatal.</summary>
    public async Task SendMetricsAsync(MetricBatch batch, CancellationToken cancellationToken = default)
    {
        await SendWriteOnlyAsync(new Request { MetricBatch = batch }, cancellationToken).ConfigureAwait(false);
    }

    public const int TransferChunkSize = 0x40000;
    public static readonly TimeSpan TransferResponseTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// FileTransmit: Begin (400→800), Chunks (401→801, stop-and-wait), End (402→802), all with
    /// header version 0 and one shared track id; then verified against a fresh media catalog.
    /// </summary>
    public async Task<MediaEntry> UploadMediaAsync(string remoteName, byte[] content, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (content.Length == 0)
            throw new ArgumentException("Empty media file.", nameof(content));
        var trackId = _requests.NextTrackId();
        Request Transfer(Action<Request> setBody)
        {
            var r = new Request { Header = new WireHeader { Version = 0, TrackId = trackId } };
            setBody(r);
            return r;
        }

        await RequireTransferOkAsync(Transfer(r => r.TransferBegin = new TransferBegin
        {
            FileName = ByteString.CopyFromUtf8(remoteName),
            FileSize = (uint)content.Length,
        }), Response.BodyOneofCase.TransferBeginStatus, cancellationToken).ConfigureAwait(false);

        for (var offset = 0; offset < content.Length; offset += TransferChunkSize)
        {
            var length = Math.Min(TransferChunkSize, content.Length - offset);
            var chunk = ByteString.CopyFrom(content, offset, length);
            await RequireTransferOkAsync(Transfer(r => r.TransferChunk = new TransferChunk { FileData = chunk }),
                Response.BodyOneofCase.TransferChunkStatus, cancellationToken).ConfigureAwait(false);
            progress?.Report((double)(offset + length) / content.Length);
        }

        await RequireTransferOkAsync(Transfer(r => r.TransferEnd = new TransferEnd
        {
            FileType = ByteString.CopyFromUtf8("media"),
            Checksum = 0,
        }), Response.BodyOneofCase.TransferEndStatus, cancellationToken).ConfigureAwait(false);

        var catalog = await GetMediaCatalogAsync(cancellationToken).ConfigureAwait(false);
        var entry = catalog.MediaFileList.FirstOrDefault(e => MediaName(e) == remoteName)
            ?? throw new DeviceProtocolException($"Upload of {remoteName} reported OK but the file is not in the media catalog.");
        if (entry.FileSize != (uint)content.Length)
            throw new DeviceProtocolException($"Uploaded {remoteName} is {entry.FileSize} bytes on the device, expected {content.Length}.");
        return entry;
    }

    /// <summary>Bare file name of a catalog entry (paths and a separate extension are both seen).</summary>
    public static string MediaName(MediaEntry entry)
    {
        var name = entry.FilePath[(entry.FilePath.LastIndexOf('/') + 1)..];
        return !string.IsNullOrEmpty(entry.FileExt) && !name.EndsWith(entry.FileExt, StringComparison.Ordinal)
            ? name + entry.FileExt
            : name;
    }

    public async Task RemoveMediaAsync(string remoteName, CancellationToken cancellationToken = default)
    {
        var request = _requests.Tracked(r => r.FileRemoval = new FileRemoval { FileName = remoteName, FileType = "media" });
        await RequireAsync(request, Response.BodyOneofCase.Acknowledgement, cancellationToken).ConfigureAwait(false);
    }

    private async Task RequireTransferOkAsync(Request request, Response.BodyOneofCase expected, CancellationToken cancellationToken)
    {
        var response = await RequireAsync(request, expected, cancellationToken, TransferResponseTimeout).ConfigureAwait(false);
        var status = expected switch
        {
            Response.BodyOneofCase.TransferBeginStatus => response.TransferBeginStatus.Status,
            Response.BodyOneofCase.TransferChunkStatus => response.TransferChunkStatus.Status,
            _ => response.TransferEndStatus.Status,
        };
        if (status != TransferStatus.Types.Code.Ok)
            throw new DeviceProtocolException($"{request.BodyCase} failed with device status {status}.");
    }

    private async Task SendOverlayTrackedAsync(OverlayLayout layout, CancellationToken cancellationToken)
    {
        var request = _requests.Tracked(r => r.OverlayLayout = layout.Clone());
        await SendWriteOnlyAsync(request, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ plumbing

    private async Task<Response> RequireAsync(Request request, Response.BodyOneofCase expected, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var trackId = request.Header?.TrackId ?? 0;
        var wait = timeout ?? TransactionTimeout;
        var response = await ExchangeAsync(request,
            r => (r.Header?.TrackId ?? 0) == trackId && (r.BodyCase == expected || IsError(r)),
            wait, cancellationToken).ConfigureAwait(false)
            ?? throw new DeviceProtocolException($"No {expected} response to {request.BodyCase} within {wait.TotalSeconds} s.");
        if (IsError(response))
            throw new DeviceProtocolException($"Device rejected {request.BodyCase}: {response.Error.Code} \"{response.Error.Why}\"");
        return response;
    }

    private static bool IsError(Response r) => r.Error is { Code: not ProtocolError.Types.Code.Success };

    private async Task<Response?> ExchangeAsync(Request request, Func<Response, bool> isAnswer, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAsync(request, cancellationToken).ConfigureAwait(false);
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < timeout)
            {
                var payload = await _transport.ReadFrameAsync(timeout - deadline.Elapsed, cancellationToken).ConfigureAwait(false);
                if (payload is null)
                    break;
                var response = Parse(payload);
                if (response is not null && isAnswer(response))
                {
                    Log?.Invoke($"<- {response.BodyCase} in {deadline.ElapsedMilliseconds} ms{(IsError(response) ? $" ERROR {response.Error}" : "")}");
                    return response;
                }
                Log?.Invoke($"<- skipped {(response is null ? $"unparseable {payload.Length} bytes" : response.BodyCase.ToString())}");
            }
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Write-only frame with an optional reply: only an explicit error is a failure.</summary>
    private async Task SendWriteOnlyAsync(Request request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAsync(request, cancellationToken).ConfigureAwait(false);
            await DrainOptionalAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DrainOptionalAsync(Request request, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < OptionalResponseWindow)
        {
            var payload = await _transport.ReadFrameAsync(OptionalResponseWindow - deadline.Elapsed, cancellationToken).ConfigureAwait(false);
            if (payload is null)
                return;
            var response = Parse(payload);
            if (response is null)
                continue;
            Log?.Invoke($"<- optional {response.BodyCase} after {request.BodyCase}{(IsError(response) ? $" ERROR {response.Error}" : "")}");
            if (IsError(response))
                throw new DeviceProtocolException($"Device rejected {request.BodyCase}: {response.Error.Code} \"{response.Error.Why}\"");
        }
    }

    private async Task WriteAsync(Request request, CancellationToken cancellationToken)
    {
        var stale = _transport.DrainQueuedFrames();
        if (stale > 0)
            Log?.Invoke($"drained {stale} stale frame(s)");
        var frame = Requests.ToFrame(request);
        Log?.Invoke($"-> {request.BodyCase} ({frame.Length} bytes)");
        using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Large FileTransmit chunks get the longer upload write deadline.
        writeTimeout.CancelAfter(frame.Length > 64 * 1024 ? TimeSpan.FromSeconds(15) : TransactionTimeout);
        await _transport.WriteFrameAsync(frame, writeTimeout.Token).ConfigureAwait(false);
        _sinceLastWrite.Restart();
    }

    private Response? Parse(byte[] payload)
    {
        try
        {
            return Response.Parser.ParseFrom(payload);
        }
        catch (InvalidProtocolBufferException ex)
        {
            Log?.Invoke($"unparseable frame ({ex.Message}): {Convert.ToHexString(payload)}");
            return null;
        }
    }

    private async Task KeepaliveLoopAsync()
    {
        var token = _shutdown.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var wait = KeepaliveInterval - _sinceLastWrite.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, token).ConfigureAwait(false);
                    continue;
                }
                // Alternate Ping and a full overlay lease, as the official bridge does.
                var lease = _leaseTurn && CurrentOverlay.LabelGroups.Count > 0;
                _leaseTurn = !_leaseTurn;
                var request = lease
                    ? new Request { Header = new WireHeader(), OverlayLayout = CurrentOverlay.Clone() }
                    : Requests.Keepalive();
                await SendWriteOnlyAsync(request, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"keepalive failed: {ex.Message}");
                await Task.Delay(500, token).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_keepaliveLoop is not null)
        {
            try
            {
                await _keepaliveLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        await _transport.DisposeAsync().ConfigureAwait(false);
        _shutdown.Dispose();
        _gate.Dispose();
    }
}
