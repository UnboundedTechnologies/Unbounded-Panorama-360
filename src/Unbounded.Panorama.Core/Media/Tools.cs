namespace Unbounded.Panorama.Media;

/// <summary>Locates the external tools (ffmpeg, ffprobe, yt-dlp).</summary>
public static class Tools
{
    private static readonly Lazy<string> Ffmpeg = new(() => Find("ffmpeg.exe", "Gyan.FFmpeg"));
    private static readonly Lazy<string> Ffprobe = new(() => Find("ffprobe.exe", "Gyan.FFmpeg"));
    private static readonly Lazy<string> YtDlp = new(() => Find("yt-dlp.exe", "yt-dlp.yt-dlp"));
    private static readonly Lazy<string?> Deno = new(() =>
    {
        try
        {
            return Find("deno.exe", "DenoLand.Deno");
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
    /// Order: next to the app (tools\), then the winget package folder, then PATH.
    /// </summary>
    private static string Find(string exe, string wingetPackagePrefix)
    {
        var local = Path.Combine(AppContext.BaseDirectory, "tools", exe);
        if (File.Exists(local))
            return local;

        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(packages))
        {
            var hit = Directory.EnumerateDirectories(packages, wingetPackagePrefix + "_*")
                .SelectMany(d => Directory.EnumerateFiles(d, exe, SearchOption.AllDirectories))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (hit is not null)
                return hit;
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), exe);
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException($"{exe} was not found. Install it with: winget install {wingetPackagePrefix}");
    }
}
