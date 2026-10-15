using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using Unbounded.Panorama.Protocol;

namespace Unbounded.Panorama.Transport;

/// <summary>
/// Overlapped I/O on the usbprint device interface. One read is kept outstanding for the
/// whole session: Panorama-family firmware drops off the bus when the host stops polling
/// bulk IN between exchanges.
/// </summary>
public sealed class UsbPrintTransport : IAsyncDisposable
{
    private const int ReadBufferSize = 64 * 1024;

    private readonly SafeFileHandle _handle;
    private readonly OverlappedDevice _device;
    private readonly FrameReassembler _reassembler = new();
    private readonly Channel<byte[]> _frames = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Task _readLoop;

    public event Action<string>? Log;

    /// <summary>Raw IN chunks, for diagnostics.</summary>
    public event Action<ReadOnlyMemory<byte>>? RawIn;

    public long BytesIn { get; private set; }
    public long BytesOut { get; private set; }
    public long DiscardedBytes => _reassembler.DiscardedBytes;
    public Exception? ReadFault { get; private set; }

    private UsbPrintTransport(SafeFileHandle handle)
    {
        _handle = handle;
        _device = new OverlappedDevice(handle);
        _readLoop = Task.Run(ReadLoopAsync);
    }

    public static UsbPrintTransport Open(string interfacePath)
    {
        const uint GENERIC_READ = 0x80000000;
        const uint GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_READ = 0x1;
        const uint FILE_SHARE_WRITE = 0x2;
        const uint OPEN_EXISTING = 3;
        const uint FILE_FLAG_OVERLAPPED = 0x40000000;

        var handle = CreateFileW(interfacePath, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"Cannot open {interfacePath}: {new Win32Exception(error).Message} (Win32 {error})");
        }
        return new UsbPrintTransport(handle);
    }

    public async Task WriteFrameAsync(byte[] frame, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var written = await _device.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            BytesOut += written;
            if (written != frame.Length)
                throw new IOException($"Short write: {written} of {frame.Length} bytes.");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Next reassembled payload (without the "TRYX" envelope).</summary>
    public async Task<byte[]?> ReadFrameAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            return await _frames.Reader.ReadAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public int DrainQueuedFrames()
    {
        var n = 0;
        while (_frames.Reader.TryRead(out _))
            n++;
        return n;
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[ReadBufferSize];
        var emptyStreak = 0;
        var errorStreak = 0;
        var token = _shutdown.Token;

        while (!token.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await _device.ReadAsync(buffer, token).ConfigureAwait(false);
                errorStreak = 0;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or Win32Exception or OperationCanceledException)
            {
                errorStreak++;
                Log?.Invoke($"IN error #{errorStreak}: {ex.Message}");
                if (errorStreak >= 10)
                {
                    ReadFault = ex;
                    break;
                }
                await Task.Delay(Math.Min(20 << (errorStreak - 1), 200), token).ConfigureAwait(false);
                continue;
            }

            if (read == 0)
            {
                // usbprint can complete a read with no data; re-arm with a short backoff.
                emptyStreak++;
                await Task.Delay(Math.Min(5 << Math.Min(emptyStreak - 1, 5), 100), token).ConfigureAwait(false);
                continue;
            }

            emptyStreak = 0;
            BytesIn += read;
            RawIn?.Invoke(buffer.AsMemory(0, read).ToArray());
            _reassembler.Append(buffer.AsSpan(0, read));
            while (_reassembler.TryTake() is { } payload)
                _frames.Writer.TryWrite(payload);
        }

        _frames.Writer.TryComplete(ReadFault);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _device.CancelAll();
        try
        {
            await _readLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _device.Dispose();
        _handle.Dispose();
        _shutdown.Dispose();
        _writeLock.Dispose();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
}
