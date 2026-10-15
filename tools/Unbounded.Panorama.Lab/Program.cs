// Hardware lab: holds one live session and executes command files dropped into <lab>/inbox.
// Each command's outcome is written to <lab>/results/<name>.txt.
//
// Commands (JSON):
//   {"op":"overlay","layout":{...OverlayLayout protobuf JSON...}}
//   {"op":"display","uiRotation":90,"mediaRotation":0}          read-modify-write of display_config
//   {"op":"ticker","groupId":100,"labelId":102,"format":"HH:mm:ss"}  live text update every second
//   {"op":"ticker-stop"}
//   {"op":"readback"}
//   {"op":"restore"}       writes the original UserConfiguration captured by the Phase 0 probe, clears overlay
//   {"op":"quit"}
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Unbounded.Panorama.Session;
using Unbounded.Panorama.Transport;
using Unbounded.Panorama.Wire;

var labDir = Path.GetFullPath(args[0]);
var originalConfigFile = Path.GetFullPath(args[1]);
var inbox = Directory.CreateDirectory(Path.Combine(labDir, "inbox")).FullName;
var results = Directory.CreateDirectory(Path.Combine(labDir, "results")).FullName;
var logFile = Path.Combine(labDir, "lab.log");

var clock = Stopwatch.StartNew();
var logLock = new object();
void Log(string line)
{
    var stamped = $"{DateTime.Now:HH:mm:ss.fff} [{clock.Elapsed.TotalSeconds,8:F2}s] {line}";
    lock (logLock)
    {
        Console.WriteLine(stamped);
        File.AppendAllText(logFile, stamped + Environment.NewLine);
    }
}

var original = Response.Parser.ParseFrom(File.ReadAllBytes(originalConfigFile)).UserConfiguration
    ?? throw new InvalidOperationException("Original config file has no UserConfiguration.");
Log($"original config loaded ({original.CalculateSize()} bytes): {original}");

var devices = DeviceDiscovery.Enumerate();
if (devices.Count != 1)
{
    Log($"expected one device, found {devices.Count}");
    return 3;
}

await using var session = await PanoramaSession.ConnectAsync(devices[0], Log);
Log("session ready");
File.WriteAllText(Path.Combine(labDir, "READY"), DateTime.Now.ToString("O"));

CancellationTokenSource? ticker = null;
var running = true;
while (running)
{
    var next = Directory.GetFiles(inbox, "*.json").OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();
    if (next is null)
    {
        await Task.Delay(200);
        continue;
    }

    var name = Path.GetFileNameWithoutExtension(next);
    var output = new StringBuilder();
    void Out(string line)
    {
        output.AppendLine(line);
        Log($"[{name}] {line}");
    }

    try
    {
        var json = File.ReadAllText(next);
        File.Delete(next);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var op = root.GetProperty("op").GetString();
        Out($"op={op}");

        switch (op)
        {
            case "overlay":
            {
                var layout = JsonParser.Default.Parse<OverlayLayout>(root.GetProperty("layout").GetRawText());
                Out($"sending overlay with {layout.LabelGroups.Count} group(s), {layout.CalculateSize()} bytes");
                await session.SetOverlayAsync(layout);
                Out("overlay accepted (no explicit error in the optional reply window)");
                break;
            }
            case "display":
            {
                var (before, after) = await session.UpdateUserConfigurationAsync(c =>
                {
                    c.DisplayConfig ??= new DisplayConfiguration();
                    if (root.TryGetProperty("uiRotation", out var ui))
                        c.DisplayConfig.UiRotation = ui.GetUInt32();
                    if (root.TryGetProperty("mediaRotation", out var media))
                        c.DisplayConfig.MediaRotation = media.GetUInt32();
                });
                Out($"before: {before.DisplayConfig}");
                Out($"after readback: {after.DisplayConfig}");
                break;
            }
            case "ticker":
            {
                ticker?.Cancel();
                ticker = new CancellationTokenSource();
                var groupId = root.GetProperty("groupId").GetUInt32();
                var labelId = root.GetProperty("labelId").GetUInt32();
                var format = root.TryGetProperty("format", out var f) ? f.GetString()! : "HH:mm:ss";
                var token = ticker.Token;
                _ = Task.Run(async () =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        var batch = new MetricBatch();
                        batch.LabelGroups.Add(new OverlayGroupUpdate
                        {
                            GroupId = groupId,
                            LabelTexts = { new OverlayLabelUpdate { LabelId = labelId, Text = DateTime.Now.ToString(format) } },
                        });
                        try
                        {
                            await session.SendMetricsAsync(batch, token);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                        catch (Exception ex)
                        {
                            Log($"ticker error: {ex.Message}");
                        }
                        await Task.Delay(1000, token).ContinueWith(_ => { });
                    }
                });
                Out($"ticker started on group {groupId} label {labelId}");
                break;
            }
            case "upload":
            {
                var file = root.GetProperty("file").GetString()!;
                var remote = root.GetProperty("name").GetString()!;
                var bytes = File.ReadAllBytes(file);
                var timer = Stopwatch.StartNew();
                var entry = await session.UploadMediaAsync(remote, bytes);
                Out($"uploaded {bytes.Length} bytes in {timer.ElapsedMilliseconds} ms; catalog entry: {entry}");
                break;
            }
            case "single":
            {
                var remote = root.GetProperty("name").GetString()!;
                var (before, after) = await session.UpdateUserConfigurationAsync(c =>
                {
                    c.WorkConfig ??= new WorkConfiguration();
                    c.WorkConfig.MediaMode = WorkConfiguration.Types.MediaMode.MediaSingle;
                    c.WorkConfig.LoopMode = WorkConfiguration.Types.LoopMode.LoopSingle;
                    c.WorkConfig.SingleModeMediaFile = remote;
                });
                Out($"before: {before.WorkConfig}");
                Out($"after readback: {after.WorkConfig}");
                break;
            }
            case "remove":
            {
                var remote = root.GetProperty("name").GetString()!;
                await session.RemoveMediaAsync(remote);
                var still = (await session.GetMediaCatalogAsync()).MediaFileList.Any(e => PanoramaSession.MediaName(e) == remote);
                Out($"removed {remote}; still in catalog: {still}");
                break;
            }
            case "ticker-stop":
                ticker?.Cancel();
                Out("ticker stopped");
                break;
            case "readback":
            {
                var config = await session.GetUserConfigurationAsync();
                Out($"user config: {config}");
                break;
            }
            case "restore":
            {
                ticker?.Cancel();
                await session.SetOverlayAsync(new OverlayLayout());
                var after = await session.WriteUserConfigurationAsync(original);
                var identical = after.Equals(original);
                Out($"restored; readback identical to original: {identical}");
                if (!identical)
                    Out($"readback: {after}");
                break;
            }
            case "quit":
                ticker?.Cancel();
                running = false;
                Out("quitting");
                break;
            default:
                Out($"unknown op");
                break;
        }
        Out("STATUS OK");
    }
    catch (Exception ex)
    {
        Out($"STATUS FAILED: {ex.GetType().Name}: {ex.Message}");
    }

    File.WriteAllText(Path.Combine(results, name + ".txt"), output.ToString());
}

return 0;
