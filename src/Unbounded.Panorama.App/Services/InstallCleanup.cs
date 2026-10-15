using System.IO;
using System.Text.RegularExpressions;

namespace Unbounded.Panorama.App.Services;

/// <summary>
/// Removes older installed versions so they never pile up. Runs in the newly started version,
/// after it has taken over (the old copy has released the single-instance lock and is exiting),
/// retrying while Windows still holds the old files.
/// </summary>
public static partial class InstallCleanup
{
    public static string InstallRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "UnboundedPanorama360");

    [GeneratedRegex(@"^app-\d{8}-\d{6}$")]
    private static partial Regex VersionFolder();

    public static async Task RunAsync(Action<string> log, CancellationToken cancellationToken = default)
    {
        var root = InstallRoot;
        if (!Directory.Exists(root))
            return;

        // Keep the version we run from and the one "current" points to (normally the same).
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var running = RealPath(AppContext.BaseDirectory);
        if (running is not null)
            keep.Add(running);
        var currentLink = Path.Combine(root, "current");
        if (Directory.Exists(currentLink) && RealPath(currentLink) is { } currentTarget)
            keep.Add(currentTarget);

        // Only clean up when this app is the installed one; a build run from elsewhere leaves installs alone.
        if (running is null || !running.StartsWith(Normalize(root), StringComparison.OrdinalIgnoreCase))
            return;

        var pending = Directory.EnumerateDirectories(root)
            .Where(d => VersionFolder().IsMatch(Path.GetFileName(d)) && !keep.Contains(Normalize(d)))
            .ToList();

        for (var attempt = 0; attempt < 30 && pending.Count > 0; attempt++)
        {
            foreach (var dir in pending.ToList())
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                    pending.Remove(dir);
                    log($"Removed old version {Path.GetFileName(dir)}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Still held by the old copy while it exits; try again shortly.
                }
            }
            if (pending.Count > 0)
                await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
        }
        foreach (var dir in pending)
            log($"Could not remove old version {Path.GetFileName(dir)} yet; it will be removed at the next start.");
    }

    /// <summary>Full path with junctions resolved (e.g. ...\current → ...\app-20261015-162142).</summary>
    private static string? RealPath(string path)
    {
        try
        {
            var dir = new DirectoryInfo(Path.TrimEndingDirectorySeparator(path));
            // Resolve the deepest link in the chain: the folder itself or any parent (current\ is a junction).
            for (var probe = dir; probe is not null; probe = probe.Parent)
            {
                if (probe.LinkTarget is not null && probe.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    var rest = Path.GetRelativePath(probe.FullName, dir.FullName);
                    return Normalize(rest == "." ? target.FullName : Path.Combine(target.FullName, rest));
                }
            }
            return Normalize(dir.FullName);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
