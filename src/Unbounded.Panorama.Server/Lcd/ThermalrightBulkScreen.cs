using System.Buffers.Binary;

namespace Unbounded.Panorama.Server.Lcd;

/// <summary>
/// Thermalright / ChiZhu screens on USB 87ad:70db (GrandVision, Frozen Warframe Pro, Mjolnir Vision…):
/// raw bulk transfers on endpoint 0x01, replies on 0x81. Written from what the device does on the
/// wire (verified on a Frozen Warframe Pro, firmware "SPISCRM-V2"):
/// <list type="bullet">
/// <item>Handshake: 64 bytes, magic 12 34 56 78 at 0 and command 1 at byte 56; the 64-byte reply
/// carries the product byte (PM) at offset 24 and the sub-model at 36.</item>
/// <item>Frame: a 64-byte header (magic, 3 at byte 4, width at 8, height at 12, command 2 at 56,
/// pixel byte count at 60, all little-endian) followed by RGB565 pixels, high byte first.</item>
/// <item>The firmware shows its logo again after about 2-3 s without a frame, so frames keep coming.</item>
/// </list>
/// </summary>
public sealed class ThermalrightBulkScreen : IScreen
{
    public const ushort VendorId = 0x87AD;
    public const ushort ProductId = 0x70DB;

    private const byte EndpointOut = 0x01;
    private const byte EndpointIn = 0x81;
    private const uint Magic = 0x78563412; // bytes 12 34 56 78
    private const int HeaderSize = 64;

    private readonly object _gate = new();
    private IntPtr _context;
    private IntPtr _handle;
    private byte[] _frame = [];

    public string Name { get; private set; } = "Thermalright screen";
    public int Width { get; private set; } = 320;
    public int Height { get; private set; } = 320;
    public bool Connected { get { lock (_gate) return _handle != IntPtr.Zero; } }
    public int ProductMode { get; private set; }
    public int SubModel { get; private set; }
    public string Firmware { get; private set; } = "";
    public string? LastError { get; private set; }

    /// <summary>Panel size for a product byte (PM). 32 = the 320×320 panels (Frozen Warframe Pro…).</summary>
    public static (int Width, int Height) Resolution(int productMode) => productMode switch
    {
        32 => (320, 320),
        _ => (480, 480), // the larger GrandVision / Mjolnir Vision panels
    };

    public bool Connect()
    {
        lock (_gate)
        {
            if (_handle != IntPtr.Zero)
                return true;
            try
            {
                if (_context == IntPtr.Zero && LibUsb.Init(out _context) != LibUsb.Success)
                {
                    LastError = "libusb could not start";
                    return false;
                }
                _handle = LibUsb.OpenDeviceWithVidPid(_context, VendorId, ProductId);
                if (_handle == IntPtr.Zero)
                {
                    LastError = "screen not found (USB 87ad:70db)";
                    return false;
                }
                LibUsb.SetAutoDetachKernelDriver(_handle, 1);
                var claim = LibUsb.ClaimInterface(_handle, 0);
                if (claim != LibUsb.Success)
                    throw new IOException("claim interface: " + LibUsb.ErrorName(claim));
                Handshake();
                _frame = new byte[HeaderSize + Width * Height * 2];
                WriteHeader(_frame, Width, Height);
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                CloseHandle();
                return false;
            }
        }
    }

    private unsafe void Handshake()
    {
        var request = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(request, Magic);
        request[56] = 1;
        Write(request, 1000);

        var reply = new byte[1024];
        int read;
        fixed (byte* p = reply)
        {
            var result = LibUsb.BulkTransfer(_handle, EndpointIn, p, reply.Length, out read, 1000);
            if (result != LibUsb.Success)
                throw new IOException("handshake reply: " + LibUsb.ErrorName(result));
        }
        if (read < 40 || BinaryPrimitives.ReadUInt32LittleEndian(reply) != Magic || reply[24] == 0)
            throw new IOException($"unexpected handshake reply ({read} bytes)");
        ProductMode = reply[24];
        SubModel = reply[36];
        Firmware = System.Text.Encoding.ASCII.GetString(reply, 4, 16).TrimEnd('\0');
        (Width, Height) = Resolution(ProductMode);
        Name = $"Thermalright {Width}×{Height} screen";
    }

    /// <summary>The 64-byte frame header, as the device expects it.</summary>
    public static void WriteHeader(Span<byte> frame, int width, int height)
    {
        frame[..HeaderSize].Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(frame, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(frame[4..], 3);
        BinaryPrimitives.WriteUInt32LittleEndian(frame[8..], (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(frame[12..], (uint)height);
        frame[56] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(frame[60..], (uint)(width * height * 2));
    }

    /// <summary>BGRA → RGB565, high byte first (00 1F is pure blue).</summary>
    public static void ToRgb565(ReadOnlySpan<byte> bgra, Span<byte> target)
    {
        for (int i = 0, o = 0; o + 1 < target.Length && i + 3 < bgra.Length; i += 4, o += 2)
        {
            var value = ((bgra[i + 2] & 0xF8) << 8) | ((bgra[i + 1] & 0xFC) << 3) | (bgra[i] >> 3);
            target[o] = (byte)(value >> 8);
            target[o + 1] = (byte)value;
        }
    }

    public void Send(ReadOnlySpan<byte> bgra)
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException("The screen is not connected.");
            ToRgb565(bgra, _frame.AsSpan(HeaderSize));
            try
            {
                Write(_frame, 2000);
            }
            catch
            {
                // Unplugged or reset: drop the handle, the display loop connects again.
                CloseHandle();
                throw;
            }
        }
    }

    private unsafe void Write(byte[] data, uint timeoutMs)
    {
        fixed (byte* p = data)
        {
            var result = LibUsb.BulkTransfer(_handle, EndpointOut, p, data.Length, out var written, timeoutMs);
            if (result != LibUsb.Success || written != data.Length)
                throw new IOException($"bulk write: {LibUsb.ErrorName(result)} ({written}/{data.Length} bytes)");
            // A transfer that is an exact multiple of the packet size ends with a zero-length packet.
            if (data.Length % 512 == 0)
                LibUsb.BulkTransfer(_handle, EndpointOut, p, 0, out _, timeoutMs);
        }
    }

    private void CloseHandle()
    {
        if (_handle == IntPtr.Zero)
            return;
        LibUsb.ReleaseInterface(_handle, 0);
        LibUsb.Close(_handle);
        _handle = IntPtr.Zero;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            CloseHandle();
            if (_context != IntPtr.Zero)
            {
                LibUsb.Exit(_context);
                _context = IntPtr.Zero;
            }
        }
    }
}
