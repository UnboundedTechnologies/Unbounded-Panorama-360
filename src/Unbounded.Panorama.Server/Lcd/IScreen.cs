namespace Unbounded.Panorama.Server.Lcd;

/// <summary>A cooler screen the server streams finished frames to.</summary>
public interface IScreen : IDisposable
{
    string Name { get; }
    int Width { get; }
    int Height { get; }
    bool Connected { get; }

    /// <summary>Opens the device and reads its identity; false when it is not there.</summary>
    bool Connect();

    /// <summary>Sends one frame: BGRA (SkiaSharp's native order), Width × Height pixels.</summary>
    void Send(ReadOnlySpan<byte> bgra);
}
