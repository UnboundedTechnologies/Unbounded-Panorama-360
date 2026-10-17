using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using OpenRGB.NET;

namespace Unbounded.Panorama.Lighting;

/// <summary>
/// Sends OpenRGB "update mode" and "save mode" requests itself, because of an inconsistency in
/// OpenRGB 1.0 (verified on hardware and in its source, RGBController::SetModeValuesFromMode): for a
/// mode without a direction the server reports direction 0xFFFFFFFF but only accepts 0, and it
/// silently ignores a request that does not validate. OpenRGB.NET echoes the reported value back, so
/// Static, Off, Breathing… never took effect (only Rainbow-like modes, which report 0, did).
/// Everything else still goes through OpenRGB.NET; this uses its own connection with the same
/// protocol version (4: no acknowledgements are sent back, those start at version 6).
/// </summary>
public sealed class OpenRgbModeWriter : IDisposable
{
    private const uint ProtocolVersion = 4;
    private const uint RequestProtocolVersion = 40;
    private const uint SetClientName = 50;
    private const uint UpdateMode = 1101;
    private const uint SaveMode = 1102;

    private const ModeFlags DirectionFlags = ModeFlags.HasDirectionLR | ModeFlags.HasDirectionUD | ModeFlags.HasDirectionHV;

    private TcpClient? _tcp;
    private NetworkStream? _stream;

    /// <summary>Switches a device to a mode (values from <paramref name="current"/>, as the server reported it).</summary>
    public void Update(int device, Mode current, uint? speed, RgbColor[] colors) => Send(UpdateMode, device, ModeFields.From(current), speed, colors);

    /// <summary>Stores a mode in the device's own memory (devices that support it).</summary>
    public void Save(int device, Mode current) => Send(SaveMode, device, ModeFields.From(current), null, []);

    private void Send(uint command, int device, ModeFields mode, uint? speed, RgbColor[] colors)
    {
        var payload = Encode(mode, speed, colors);
        var packet = new byte[16 + payload.Length];
        Encoding.ASCII.GetBytes("ORGB").CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)device);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), command);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), (uint)payload.Length);
        payload.CopyTo(packet, 16);
        try
        {
            Stream().Write(packet);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // The service restarted: connect again once.
            Close();
            Stream().Write(packet);
        }
    }

    /// <summary>
    /// The mode request, field by field as OpenRGB 1.0 reads it at protocol 4 (RGBController::SetModeDescription):
    /// size, mode index, then the mode description.
    /// </summary>
    public static byte[] Encode(ModeFields mode, uint? speed, RgbColor[] colors)
    {
        var name = Encoding.ASCII.GetBytes(mode.Name);
        var modeColors = mode.ColorMode == (uint)ColorMode.ModeSpecific && colors.Length > 0 ? colors : mode.Colors;
        // A mode without a direction must say 0 (the server reports 0xFFFFFFFF but rejects it).
        var direction = (mode.Flags & (uint)DirectionFlags) != 0 ? mode.Direction : 0u;

        using var body = new MemoryStream();
        using var w = new BinaryWriter(body);
        w.Write(0u); // size, filled in below
        w.Write((uint)mode.Index);
        w.Write((ushort)(name.Length + 1));
        w.Write(name);
        w.Write((byte)0);
        w.Write(mode.Value);
        w.Write(mode.Flags);
        w.Write(mode.SpeedMin);
        w.Write(mode.SpeedMax);
        w.Write(mode.BrightnessMin);
        w.Write(mode.BrightnessMax);
        w.Write(mode.ColorMin);
        w.Write(mode.ColorMax);
        w.Write(speed ?? mode.Speed);
        w.Write(mode.Brightness);
        w.Write(direction);
        w.Write(mode.ColorMode);
        w.Write((ushort)modeColors.Length);
        foreach (var c in modeColors)
        {
            w.Write(c.R);
            w.Write(c.G);
            w.Write(c.B);
            w.Write((byte)0);
        }
        w.Flush();
        var bytes = body.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)bytes.Length);
        return bytes;
    }

    private NetworkStream Stream()
    {
        if (_stream is not null)
            return _stream;
        _tcp = new TcpClient { NoDelay = true, ReceiveTimeout = 2000, SendTimeout = 2000 };
        _tcp.Connect("127.0.0.1", RgbHub.Port);
        _stream = _tcp.GetStream();

        // Same handshake as OpenRGB.NET: agree on protocol 4 (the reply carries the server's version), then a name.
        var version = new byte[20];
        Encoding.ASCII.GetBytes("ORGB").CopyTo(version, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(version.AsSpan(8), RequestProtocolVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(version.AsSpan(12), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(version.AsSpan(16), ProtocolVersion);
        _stream.Write(version);
        var reply = new byte[20];
        _stream.ReadExactly(reply);

        var clientName = Encoding.ASCII.GetBytes("Unbounded Panorama 360 (modes)\0");
        var nameHeader = new byte[16];
        Encoding.ASCII.GetBytes("ORGB").CopyTo(nameHeader, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(nameHeader.AsSpan(8), SetClientName);
        BinaryPrimitives.WriteUInt32LittleEndian(nameHeader.AsSpan(12), (uint)clientName.Length);
        _stream.Write(nameHeader);
        _stream.Write(clientName);
        return _stream;
    }

    /// <summary>
    /// Forgets the connection (the service restarted): a write to a peer that has gone can still
    /// succeed once and be lost, so the next request opens a new connection instead.
    /// </summary>
    public void Reset() => Close();

    private void Close()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        _stream = null;
        _tcp = null;
    }

    public void Dispose() => Close();
}

/// <summary>The fields of an OpenRGB mode as sent on the wire (OpenRGB.NET's Mode cannot be created outside it).</summary>
public sealed record ModeFields(int Index, string Name, uint Value, uint Flags, uint SpeedMin, uint SpeedMax, uint BrightnessMin, uint BrightnessMax,
    uint ColorMin, uint ColorMax, uint Speed, uint Brightness, uint Direction, uint ColorMode, RgbColor[] Colors)
{
    public static ModeFields From(Mode m) => new(m.Index, m.Name, (uint)m.Value, (uint)m.Flags, m.SpeedMin, m.SpeedMax, m.BrightnessMin, m.BrightnessMax,
        m.ColorMin, m.ColorMax, m.Speed, m.Brightness, (uint)m.Direction, (uint)m.ColorMode, [.. m.Colors.Select(c => new RgbColor(c.R, c.G, c.B))]);
}
