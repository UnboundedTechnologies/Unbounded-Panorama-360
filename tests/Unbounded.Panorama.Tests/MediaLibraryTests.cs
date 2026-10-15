using System.IO;
using Unbounded.Panorama.App.Services;
using Xunit;

namespace Unbounded.Panorama.Tests;

public class MediaLibraryTests
{
    [Fact]
    public void Prune_removes_only_cache_files_no_entry_uses()
    {
        var root = Path.Combine(Path.GetTempPath(), "upn360-lib-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var roaming = Directory.CreateDirectory(Path.Combine(root, "roaming")).FullName;
            var local = Directory.CreateDirectory(Path.Combine(root, "local")).FullName;
            var picture = Path.Combine(root, "picture.png");
            File.WriteAllBytes(picture, [1, 2, 3]);

            var library = new MediaLibrary(roaming, local);
            library.Remember(LibraryKind.File, picture, "Picture");
            var thumbs = Path.Combine(local, "thumbs");

            // The thumbnail name the library uses for this entry, created as if generated.
            var thumb = library.GetType().GetMethod("ThumbPath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(library, [library.Entries[0]]) as string;
            File.WriteAllBytes(thumb!, [0]);
            var orphanJpg = Path.Combine(thumbs, "0123456789ABCDEF.jpg");
            var orphanClip = Path.Combine(thumbs, "clip-0123456789ABCDEF.mp4");
            File.WriteAllBytes(orphanJpg, [0]);
            File.WriteAllBytes(orphanClip, [0]);

            library.PruneCache();

            Assert.True(File.Exists(thumb));
            Assert.False(File.Exists(orphanJpg));
            Assert.False(File.Exists(orphanClip));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
