namespace Unbounded.Panorama.Media;

/// <summary>Locates the external tools (ffmpeg, ffprobe, yt-dlp, deno).</summary>
public static class Tools
{
    // winget package folder prefixes, in order of preference.
    private static readonly string[] FfmpegPackages = ["Gyan.FFmpeg", "yt-dlp.FFmpeg", "BtbN.FFmpeg"];

    private static readonly Lazy<string> Ffmpeg = new(() => Find("ffmpeg.exe", FfmpegPackages, "Gyan.FFmpeg.Essentials"));
    private static readonly Lazy<string> Ffprobe = new(() => Find("ffprobe.exe", FfmpegPackages, "Gyan.FFmpeg.Essentials"));
    private static readonly Lazy<string> YtDlp = new(() => Find("yt-dlp.exe", ["yt-dlp.yt-dlp"], "yt-dlp.yt-dlp"));
    private static readonly Lazy<string?> Deno = new(() =>
    {
        try
        {
            return Find("deno.exe", ["DenoLand.Deno"], "DenoLand.Deno");
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    });

    public static string FfmpegPath => Ffmpeg.Value;
    public static string FfprobePath => Ffprobe.Value;
    public static string YtDlpPath => YtDlp.Value;
    public static string? DenoPath => Deno.Value;

    /// <summary>
    /// Order: next to the app (tools\), winget's per-user and machine-wide package folders and
    /// links, then PATH.
    /// </summary>
    private static string Find(string exe, string[] wingetPrefixes, string installHint)
    {
        var local = Path.Combine(AppContext.BaseDirectory, "tools", exe);
        if (File.Exists(local))
            return local;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var packages in new[] { Path.Combine(localAppData, "Microsoft", "WinGet", "Packages"), Path.Combine(programFiles, "WinGet", "Packages") })
        {
            if (!Directory.Exists(packages))
                continue;
            foreach (var prefix in wingetPrefixes)
            {
                // "Gyan.FFmpeg*" covers both Gyan.FFmpeg_… and Gyan.FFmpeg.Essentials_….
                var hit = Directory.EnumerateDirectories(packages, prefix + "*")
                    .SelectMany(d => SafeFiles(d, exe))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (hit is not null)
                    return hit;
            }
        }

        foreach (var links in new[] { Path.Combine(localAppData, "Microsoft", "WinGet", "Links"), Path.Combine(programFiles, "WinGet", "Links") })
        {
            var candidate = Path.Combine(links, exe);
            if (File.Exists(candidate))
                return candidate;
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), exe);
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException($"{exe} was not found. Re-run the Unbounded Panorama 360 setup, or install it with: winget install {installHint}");
    }

    private static IEnumerable<string> SafeFiles(string dir, string exe)
    {
        try
        {
            return Directory.EnumerateFiles(dir, exe, SearchOption.AllDirectories).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }
}
