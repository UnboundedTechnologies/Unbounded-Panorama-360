using System.Security.Cryptography;
using Google.Protobuf;
using Unbounded.Panorama.Wire;

namespace Unbounded.Panorama.Protocol;

/// <summary>Builds request payloads in the exact shapes the device expects.</summary>
public sealed class Requests
{
    private ulong _nextTrackId;

    public Requests()
    {
        // Random 64-bit seed, incremented per tracked request; 0 is reserved for untracked frames.
        _nextTrackId = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
    }

    public ulong NextTrackId()
    {
        var id = _nextTrackId++;
        return id == 0 ? _nextTrackId++ : id;
    }

    /// <summary>Bootstrap frames: header {version=1}, no track id.</summary>
    public static Request Bootstrap(Action<Request> setBody)
    {
        var request = new Request { Header = new WireHeader { Version = 1 } };
        setBody(request);
        return request;
    }

    public static Request DeviceInformationQuery() =>
        Bootstrap(r => r.DeviceInformationQuery = new QueryToken { Dummy = "NA" });

    public static Request SystemConfigurationQuery() =>
        Bootstrap(r => r.SystemConfigurationQuery = new QueryToken { Dummy = "NA" });

    public static Request DeviceAuthenticationQuery() =>
        Bootstrap(r => r.DeviceAuthenticationQuery = new DeviceAuthQuery { Key = 1 });

    /// <summary>Keepalive: present-but-empty header, Ping{"hello?"}.</summary>
    public static Request Keepalive() => new()
    {
        Header = new WireHeader(),
        Ping = new Ping { Payload = ByteString.CopyFromUtf8("hello?") },
    };

    public Request Tracked(Action<Request> setBody, uint version = 1)
    {
        var request = new Request { Header = new WireHeader { Version = version, TrackId = NextTrackId() } };
        setBody(request);
        return request;
    }

    public Request MediaCatalogQuery() => Tracked(r => r.MediaCatalogQuery = new QueryToken());

    public Request UserConfigurationQuery() => Tracked(r => r.UserConfigurationQuery = new QueryToken());

    public static byte[] ToFrame(Request request) => FrameCodec.Encode(request.ToByteArray());
}
