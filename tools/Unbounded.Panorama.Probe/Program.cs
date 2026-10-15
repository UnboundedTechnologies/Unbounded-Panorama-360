// Phase 0 hardware probe. Read-only: it sends queries and a keepalive Ping only.
// It never writes user configuration, overlay layout, metrics, or files.
using System.Diagnostics;
using System.Text;
using Google.Protobuf;
using Unbounded.Panorama.Protocol;
using Unbounded.Panorama.Transport;
using Unbounded.Panorama.Wire;

var outDir = Path.Combine(AppContext.BaseDirectory, "probe-results", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
if (args.Length > 0)
    outDir = Path.GetFullPath(args[0]);
Directory.CreateDirectory(outDir);

var log = new StringBuilder();
var clock = Stopwatch.StartNew();
void Log(string line)
{
    var stamped = $"[{clock.Elapsed.TotalSeconds,7:F3}s] {line}";
    Console.WriteLine(stamped);
    log.AppendLine(stamped);
}

// 1. Self-check: our encoder must reproduce the reference test vectors byte for byte.
var expected = new Dictionary<string, (Request Request, string Hex)>
{
    ["DeviceInfo"] = (Requests.DeviceInformationQuery(), "545259580B0000000A020801A206040A024E41"),
    ["SysConfig"] = (Requests.SystemConfigurationQuery(), "545259580B0000000A020801B206040A024E41"),
    ["DeviceAuth"] = (Requests.DeviceAuthenticationQuery(), "54525958090000000A020801AA06020801"),
    ["Keepalive"] = (Requests.Keepalive(), "545259580C0000000A0052080A0668656C6C6F3F"),
};
var selfCheckOk = true;
foreach (var (name, (request, hex)) in expected)
{
    var actual = Convert.ToHexString(Requests.ToFrame(request));
    var ok = actual == hex;
    selfCheckOk &= ok;
    Log($"self-check {name}: {(ok ? "OK" : $"MISMATCH got {actual} expected {hex}")}");
}
if (!selfCheckOk)
{
    Log("Encoder self-check failed; refusing to talk to hardware.");
    File.WriteAllText(Path.Combine(outDir, "probe.log"), log.ToString());
    return 2;
}

// 2. Discovery.
var devices = DeviceDiscovery.Enumerate();
Log($"found {devices.Count} TRYX printer-class interface(s)");
foreach (var d in devices)
    Log($"  {d.ProductName} {d.VendorId:x4}:{d.ProductId:x4} serial={d.Serial} path={d.InterfacePath}");
if (devices.Count != 1)
{
    Log("Expected exactly one device; stopping.");
    File.WriteAllText(Path.Combine(outDir, "probe.log"), log.ToString());
    return 3;
}

// 3. Session.
var device = devices[0];
await using var transport = UsbPrintTransport.Open(device.InterfacePath);
transport.Log += m => Log($"transport: {m}");
var rawIn = new List<string>();
transport.RawIn += chunk => { lock (rawIn) rawIn.Add(Convert.ToHexString(chunk.Span)); };
Log("opened interface; read loop armed");

var requests = new Requests();
var step = 0;
var results = new List<string>();

async Task<Response?> Exchange(string name, Request request, Func<Response, bool> isAnswer, TimeSpan timeout)
{
    step++;
    var frame = Requests.ToFrame(request);
    var stale = transport.DrainQueuedFrames();
    if (stale > 0)
        Log($"{name}: drained {stale} stale frame(s)");
    Log($"{name}: OUT {frame.Length} bytes {Convert.ToHexString(frame)}");
    var writeTimer = Stopwatch.StartNew();
    using (var writeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
    {
        try
        {
            await transport.WriteFrameAsync(frame, writeCts.Token);
        }
        catch (Exception ex)
        {
            Log($"{name}: WRITE FAILED after {writeTimer.ElapsedMilliseconds} ms: {ex.GetType().Name}: {ex.Message}");
            results.Add($"{name}: write failed ({ex.Message})");
            return null;
        }
    }
    Log($"{name}: write completed in {writeTimer.ElapsedMilliseconds} ms");

    var deadline = Stopwatch.StartNew();
    while (deadline.Elapsed < timeout)
    {
        var payload = await transport.ReadFrameAsync(timeout - deadline.Elapsed, CancellationToken.None);
        if (payload is null)
            break;
        var file = Path.Combine(outDir, $"{step:D2}-{name}-response.bin");
        Response response;
        try
        {
            response = Response.Parser.ParseFrom(payload);
        }
        catch (InvalidProtocolBufferException ex)
        {
            Log($"{name}: IN {payload.Length} bytes, not a Response ({ex.Message}): {Convert.ToHexString(payload)}");
            continue;
        }
        Log($"{name}: IN {payload.Length} bytes after {deadline.ElapsedMilliseconds} ms, body={response.BodyCase}, header=[{response.Header}], error=[{response.Error}]");
        if (!isAnswer(response))
        {
            Log($"{name}: not the expected answer; continuing to wait");
            File.WriteAllBytes(file.Replace("-response.bin", $"-unexpected-{response.BodyCase}.bin"), payload);
            continue;
        }
        File.WriteAllBytes(file, payload);
        File.WriteAllText(Path.ChangeExtension(file, ".decoded.json"), JsonFormatter.Default.Format(response));
        File.WriteAllText(Path.ChangeExtension(file, ".raw-fields.txt"), ProtoDump.Dump(payload));
        results.Add($"{name}: OK ({response.BodyCase}) in {deadline.ElapsedMilliseconds} ms");
        return response;
    }
    Log($"{name}: NO ANSWER within {timeout.TotalMilliseconds} ms");
    results.Add($"{name}: no answer");
    return null;
}

var t3 = TimeSpan.FromSeconds(3);

// DeviceInfo readiness: retry a few times, as the reference app does during its 20 s window.
Response? info = null;
foreach (var backoff in new[] { 0, 500, 1000, 2000, 2000 })
{
    if (backoff > 0)
        await Task.Delay(backoff);
    info = await Exchange("DeviceInfo", Requests.DeviceInformationQuery(),
        r => r.BodyCase == Response.BodyOneofCase.DeviceInformation, t3);
    if (info is not null)
        break;
}

if (info is not null)
{
    Log($"device: {info.DeviceInformation}");
    await Exchange("SysConfig", Requests.SystemConfigurationQuery(),
        r => r.BodyCase == Response.BodyOneofCase.SystemConfiguration, t3);
    await Exchange("DeviceAuth", Requests.DeviceAuthenticationQuery(),
        r => r.BodyCase == Response.BodyOneofCase.DeviceAuthentication, t3);
    await Exchange("Ping", Requests.Keepalive(),
        r => r.BodyCase == Response.BodyOneofCase.Pong, TimeSpan.FromMilliseconds(1500));

    var uc = requests.UserConfigurationQuery();
    await Exchange("UserConfig", uc,
        r => r.BodyCase == Response.BodyOneofCase.UserConfiguration && r.Header?.TrackId == uc.Header.TrackId, t3);

    var mc = requests.MediaCatalogQuery();
    await Exchange("MediaCatalog", mc,
        r => r.BodyCase == Response.BodyOneofCase.MediaCatalog && r.Header?.TrackId == mc.Header.TrackId, t3);

    await Exchange("Ping2", Requests.Keepalive(),
        r => r.BodyCase == Response.BodyOneofCase.Pong, TimeSpan.FromMilliseconds(1500));

    // Hold the link for a few seconds with keepalives, watching for anything unsolicited.
    for (var i = 0; i < 3; i++)
    {
        await Task.Delay(2000);
        await transport.WriteFrameAsync(Requests.ToFrame(Requests.Keepalive()), CancellationToken.None);
        while (await transport.ReadFrameAsync(TimeSpan.FromMilliseconds(250), CancellationToken.None) is { } extra)
        {
            var r = Response.Parser.ParseFrom(extra);
            Log($"idle keepalive {i + 1}: IN body={r.BodyCase} header=[{r.Header}]");
        }
    }
}

Log("---- summary ----");
foreach (var r in results)
    Log(r);
Log($"bytes out={transport.BytesOut} in={transport.BytesIn} discarded-before-magic={transport.DiscardedBytes} readFault={transport.ReadFault?.Message ?? "none"}");

File.WriteAllText(Path.Combine(outDir, "probe.log"), log.ToString());
lock (rawIn)
    File.WriteAllLines(Path.Combine(outDir, "raw-in-chunks.txt"), rawIn);
Console.WriteLine($"Results written to {outDir}");
return info is null ? 1 : 0;
