using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace Unbounded.Panorama.Lighting;

/// <summary>
/// Listens on its own connection for OpenRGB's "device list updated" notice (packet 100), sent when
/// a device is plugged in, unplugged or detected again. OpenRGB.NET 3.1.1 does not raise its
/// DeviceListUpdated event for it (verified: the server sent it six times during a rescan, the event
/// never fired). Reconnects by itself when the service restarts.
/// </summary>
public sealed class OpenRgbWatcher : IDisposable
{
    private const uint RequestProtocolVersion = 40;
    private const uint SetClientName = 50;
    private const uint DeviceListUpdated = 100;

    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private TcpClient? _tcp;

    /// <summary>Raised on a background thread.</summary>
    public event Action? DeviceListChanged;

    public OpenRgbWatcher() => new Thread(Run) { IsBackground = true, Name = "OpenRGB watcher" }.Start();

    private void Run()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var tcp = new TcpClient { NoDelay = true };
                lock (_gate)
                {
                    if (_stop.IsCancellationRequested)
                        return;
                    _tcp = tcp;
                }
                tcp.Connect("127.0.0.1", RgbHub.Port);
                var stream = tcp.GetStream();
                stream.Write(Header(RequestProtocolVersion, 4));
                stream.Write(BitConverter.GetBytes(4u));
                var name = Encoding.ASCII.GetBytes("Unbounded Panorama 360 (watcher)\0");
                var header = new byte[16];
                while (true)
                {
                    stream.ReadExactly(header);
                    var id = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
                    var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
                    if (length > 0)
                        stream.ReadExactly(new byte[length]);
                    if (id == RequestProtocolVersion)
                    {
                        stream.Write(Header(SetClientName, (uint)name.Length));
                        stream.Write(name);
                    }
                    else if (id == DeviceListUpdated)
                    {
                        DeviceListChanged?.Invoke();
                    }
                }
            }
            catch (Exception) when (!_stop.IsCancellationRequested)
            {
                // Service not running or restarted: try again shortly.
            }
            catch (Exception)
            {
                return;
            }
            finally
            {
                lock (_gate)
                {
                    _tcp?.Dispose();
                    _tcp = null;
                }
            }
            _stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
        }
    }

    private static byte[] Header(uint id, uint length)
    {
        var b = new byte[16];
        Encoding.ASCII.GetBytes("ORGB").CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), id);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), length);
        return b;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stop.Cancel();
            _tcp?.Dispose();
            _tcp = null;
        }
    }
}
