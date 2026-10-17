using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Unbounded.Panorama.Layout;
using Unbounded.Panorama.Lighting;
using Unbounded.Panorama.Media;
using Unbounded.Panorama.Sensors;
using Unbounded.Panorama.Server.Display;
using Unbounded.Panorama.Server.Lcd;
using Unbounded.Panorama.Server.Library;
using Unbounded.Panorama.Server.Lighting;
using Unbounded.Panorama.Server.Model;
using Unbounded.Panorama.Server.Sensors;
using Unbounded.Panorama.Server.Storage;
using Unbounded.Panorama.Server.Web;

if (args.Contains("--test-pattern"))
    return TestPattern.Run();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSystemd();
var dataDir = Environment.GetEnvironmentVariable("UPN_DATA") ?? (OperatingSystem.IsWindows() ? Path.Combine(AppContext.BaseDirectory, "data") : "/var/lib/unbounded-panorama");
var configDir = Environment.GetEnvironmentVariable("UPN_CONFIG") ?? "/etc/unbounded-panorama";
var port = int.TryParse(Environment.GetEnvironmentVariable("UPN_PORT"), out var p) ? p : 8360;
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
const long maxUpload = 4L * 1024 * 1024 * 1024;
builder.Services.Configure<KestrelServerOptions>(o => o.Limits.MaxRequestBodySize = maxUpload);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = maxUpload);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    foreach (var c in Json.Options.Converters)
        o.SerializerOptions.Converters.Add(c);
    o.SerializerOptions.NumberHandling = Json.Options.NumberHandling;
});

var store = new DataStore(dataDir);
var proxmox = ProxmoxStats.FromEnvFile(Path.Combine(configDir, "proxmox.env"));
var sensors = new LinuxSensors(proxmox);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton(sensors);
builder.Services.AddSingleton<ISensorReadings>(sensors);
builder.Services.AddSingleton(new WidgetText(sensors));
builder.Services.AddSingleton<IScreen, ThermalrightBulkScreen>();
builder.Services.AddSingleton<MediaLibrary>();
builder.Services.AddSingleton<JobList>();
builder.Services.AddSingleton<Auth>();
builder.Services.AddSingleton<DisplayService>();
builder.Services.AddHostedService(s => s.GetRequiredService<DisplayService>());
builder.Services.AddSingleton<LightingService>();
builder.Services.AddHostedService(s => s.GetRequiredService<LightingService>());

var app = builder.Build();
var log = app.Logger;

// Password (optional): everything under /api except signing in needs the session cookie.
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    if (path.StartsWithSegments("/api") && !path.StartsWithSegments("/api/auth")
        && !context.RequestServices.GetRequiredService<Auth>().IsSignedIn(context))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.Combine(AppContext.BaseDirectory, "Fonts")),
    RequestPath = "/fonts",
});

var api = app.MapGroup("/api");

// ------------------------------------------------------------------ sign in
api.MapGet("/auth", (Auth auth, HttpContext c) => new { required = auth.Required, signedIn = auth.IsSignedIn(c) });
api.MapPost("/auth", (Auth auth, HttpContext c, PasswordRequest body) =>
{
    if (!auth.Required)
        return Results.Ok();
    if (!auth.Verify(body.Password ?? ""))
        return Results.Unauthorized();
    c.Response.Cookies.Append(Auth.Cookie, auth.Token(), new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, MaxAge = TimeSpan.FromDays(365) });
    return Results.Ok();
});
api.MapDelete("/auth", (HttpContext c) => { c.Response.Cookies.Delete(Auth.Cookie); return Results.Ok(); });

// ------------------------------------------------------------------ status, design, preview
api.MapGet("/status", (DisplayService display, LightingService lighting, DataStore s) => new
{
    version = typeof(Program).Assembly.GetName().Version?.ToString(3),
    screen = new { display.Connected, name = display.ScreenName, display.Width, display.Height, fps = Math.Round(display.FramesPerSecond, 1), error = display.LastError, media = display.MediaStatus },
    proxmox = proxmox is null ? null : new { proxmox.Working, error = proxmox.LastError },
    lighting = new { lighting.Connected, lighting.Status },
    revision = s.Design.Revision,
});

api.MapGet("/design", (DataStore s) => s.Design);
api.MapPut("/design", (DataStore s, PanelDesign design) =>
{
    design.HasDrawing = File.Exists(s.DrawingFile) && design.HasDrawing;
    return s.SaveDesign(design);
});

api.MapGet("/preview.jpg", (DisplayService display, HttpContext c) =>
{
    c.Response.Headers.CacheControl = "no-store";
    return display.PreviewJpeg() is { } jpeg ? Results.File(jpeg, "image/jpeg") : Results.NoContent();
});

api.MapGet("/drawing", (DataStore s) => File.Exists(s.DrawingFile) ? Results.File(s.DrawingFile, "image/png") : Results.NotFound());
api.MapPut("/drawing", async (DataStore s, HttpRequest request) =>
{
    var temp = s.DrawingFile + ".tmp";
    await using (var file = File.Create(temp))
        await request.Body.CopyToAsync(file);
    File.Move(temp, s.DrawingFile, overwrite: true);
    var design = s.Design.Clone();
    design.HasDrawing = true;
    return s.SaveDesign(design);
});
api.MapDelete("/drawing", (DataStore s) =>
{
    File.Delete(s.DrawingFile);
    var design = s.Design.Clone();
    design.HasDrawing = false;
    return s.SaveDesign(design);
});

// ------------------------------------------------------------------ settings
api.MapGet("/settings", (DataStore s) => SettingsView(s.Settings));
api.MapPut("/settings", (DataStore s, SettingsRequest body) =>
{
    var settings = s.Settings;
    var next = new PanelSettings
    {
        Brightness = Math.Clamp(body.Brightness ?? settings.Brightness, 0, 100),
        Rotation = body.Rotation is 0 or 90 or 180 or 270 ? body.Rotation.Value : settings.Rotation,
        FramesPerSecond = Math.Clamp(body.FramesPerSecond ?? settings.FramesPerSecond, 5, 30),
        Fahrenheit = body.Fahrenheit ?? settings.Fahrenheit,
        On = body.On ?? settings.On,
        PasswordHash = settings.PasswordHash,
    };
    s.SaveSettings(next);
    return SettingsView(next);
});
api.MapPost("/settings/password", (DataStore s, Auth auth, HttpContext c, PasswordChange body) =>
{
    var settings = s.Settings;
    if (auth.Required && !auth.Verify(body.Current ?? ""))
        return Results.BadRequest(new { error = "The current password is not right." });
    var next = new PanelSettings
    {
        Brightness = settings.Brightness, Rotation = settings.Rotation, FramesPerSecond = settings.FramesPerSecond,
        Fahrenheit = settings.Fahrenheit, On = settings.On,
        PasswordHash = string.IsNullOrEmpty(body.Password) ? null : Auth.Hash(body.Password),
    };
    s.SaveSettings(next);
    if (next.PasswordHash is not null)
        c.Response.Cookies.Append(Auth.Cookie, auth.Token(), new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, MaxAge = TimeSpan.FromDays(365) });
    return Results.Ok(SettingsView(next));
});

// ------------------------------------------------------------------ sensors
api.MapGet("/sensors", (LinuxSensors sensors, DataStore s) => sensors.Catalog.Select(i => new
{
    i.Id, i.Category, i.Hardware, i.Name, i.Type, i.Unit,
    value = sensors.Format(i.Id, i.Type == "Temperature" || i.Unit == "%" ? 0 : 1, true, s.Settings.Fahrenheit),
}));
api.MapGet("/text", (WidgetText text, DataStore s) =>
    s.Design.Widgets.ToDictionary(w => w.Id.ToString(), w => text.Resolve(w, s.Settings.Fahrenheit)));

// ------------------------------------------------------------------ media library
api.MapGet("/library", (MediaLibrary library) => library.Items);
api.MapPost("/library", async (MediaLibrary library, JobList jobs, HttpRequest request) =>
{
    var form = await request.ReadFormAsync();
    var started = new List<object>();
    foreach (var file in form.Files)
    {
        if (!MediaLibrary.IsAllowed(file.FileName))
            continue;
        var temp = Path.Combine(store.CacheDir, "upload-" + Guid.NewGuid().ToString("N") + Path.GetExtension(file.FileName));
        await using (var target = File.Create(temp))
            await file.CopyToAsync(target);
        var name = file.FileName;
        var job = jobs.Start("Adding " + name, async (job, token) =>
        {
            job.Report(new ProgressInfo("Reading the file…"));
            return (await library.AddAsync(temp, name, null, token)).Id;
        }, log);
        started.Add(new { job.Id, job.Title });
    }
    return started.Count > 0 ? Results.Ok(started) : Results.BadRequest(new { error = "Pictures (jpg, png, gif, webp) and videos (mp4, mkv, webm, mov) only." });
});
api.MapGet("/library/{id}/file", (MediaLibrary library, string id) =>
    library.Find(id) is { } item ? Results.File(library.PathOf(item), MimeOf(item.File), enableRangeProcessing: true) : Results.NotFound());
api.MapGet("/library/{id}/thumb", (MediaLibrary library, string id) =>
    library.Find(id) is { } item && File.Exists(library.ThumbOf(item)) ? Results.File(library.ThumbOf(item), "image/jpeg") : Results.NotFound());
api.MapPatch("/library/{id}", (MediaLibrary library, string id, RenameRequest body) => library.Rename(id, body.Name ?? "") ? Results.Ok() : Results.NotFound());
api.MapDelete("/library/{id}", (MediaLibrary library, DataStore s, string id) =>
{
    if (!library.Delete(id))
        return Results.NotFound();
    if (s.Design.Background.MediaId == id)
    {
        var design = s.Design.Clone();
        design.Background.MediaId = null;
        s.SaveDesign(design);
    }
    return Results.Ok();
});

api.MapPost("/youtube/probe", async (YouTubeRequest body, CancellationToken token) =>
{
    try
    {
        var video = await new YouTubeImporter(Path.Combine(store.CacheDir, "downloads")).ProbeAsync(body.Url ?? "", token);
        return Results.Ok(new { video.Title, video.Channel, video.DurationSeconds, video.ThumbnailUrl, best = video.BestDescription });
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});
api.MapPost("/youtube/import", (MediaLibrary library, JobList jobs, YouTubeRequest body) =>
{
    var job = jobs.Start("YouTube import", async (job, token) =>
        (await library.ImportYouTubeAsync(body.Url ?? "", Math.Max(0, body.Start), Math.Max(1, body.Length), job, token)).Id, log);
    return new { job.Id, job.Title };
});

api.MapGet("/jobs", (JobList jobs) => jobs.All());
api.MapDelete("/jobs/{id}", (JobList jobs, string id) => jobs.Cancel(id) ? Results.Ok() : Results.NotFound());

// ------------------------------------------------------------------ ARGB
api.MapGet("/lighting", (LightingService lighting) => new
{
    lighting.Connected,
    lighting.Status,
    profile = lighting.Profile,
    profiles = lighting.ProfileNames(),
    syncEffects = LightEffects.For(lighting.Devices),
    devices = lighting.Devices.Select(d => new
    {
        d.Key, d.Name, d.Type, d.Vendor, d.LedCount,
        effects = LightEffects.For([d]),
        modes = d.Modes.Select(m => new { m.Name, m.HasSpeed, color = m.ModeColor || m.PerLedColor }),
        zones = d.Zones.Select(z => new { z.Name, z.LedCount, z.LedsMin, z.LedsMax, z.Resizable }),
    }),
});
api.MapPut("/lighting", (LightingService lighting, LightingProfile profile) => { lighting.Update(profile); return Results.Ok(); });
api.MapPost("/lighting/refresh", async (LightingService lighting) => { await lighting.RefreshAsync(apply: true); return Results.Ok(); });
api.MapPost("/lighting/profiles", (LightingService lighting, RenameRequest body) => lighting.SaveProfile(body.Name ?? "") ? Results.Ok() : Results.BadRequest());
api.MapPost("/lighting/profiles/{name}/load", (LightingService lighting, string name) => lighting.LoadProfile(name) ? Results.Ok() : Results.NotFound());
api.MapDelete("/lighting/profiles/{name}", (LightingService lighting, string name) => lighting.DeleteProfile(name) ? Results.Ok() : Results.NotFound());
api.MapPost("/lighting/save-to-devices", (LightingService lighting) => new { saved = lighting.SaveToDevices() });

app.MapFallbackToFile("index.html");
app.Run();
return 0;

static object SettingsView(PanelSettings s) => new { s.Brightness, s.Rotation, s.FramesPerSecond, s.Fahrenheit, s.On, hasPassword = s.PasswordHash is not null };

static string MimeOf(string file) => Path.GetExtension(file).ToLowerInvariant() switch
{
    ".jpg" or ".jpeg" => "image/jpeg",
    ".png" => "image/png",
    ".gif" => "image/gif",
    ".webp" => "image/webp",
    ".bmp" => "image/bmp",
    ".webm" => "video/webm",
    ".mkv" => "video/x-matroska",
    ".mov" => "video/quicktime",
    _ => "video/mp4",
};

internal sealed record PasswordRequest(string? Password);
internal sealed record PasswordChange(string? Current, string? Password);
internal sealed record SettingsRequest(int? Brightness, int? Rotation, int? FramesPerSecond, bool? Fahrenheit, bool? On);
internal sealed record RenameRequest(string? Name);
internal sealed record YouTubeRequest(string? Url, double Start, double Length);
