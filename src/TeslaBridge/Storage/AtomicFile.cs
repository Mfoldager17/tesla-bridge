using System.Text;

namespace TeslaBridge.Storage;

/// <summary>
/// Writing the bridge's files in /data and /config so that a crash or a full disk never leaves half a file: the
/// content goes to a temporary file next to it, is flushed to disk, and then replaces the old file in one step.
/// </summary>
public static class AtomicFile
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>
    /// Replaces <paramref name="path"/> with <paramref name="content"/>. <paramref name="secret"/>: the file is
    /// readable by its owner only (0600) from the moment it is created.
    /// </summary>
    public static void WriteAllText(string path, string content, bool secret = false)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmp = full + ".tmp";
        try
        {
            File.Delete(tmp);   // a leftover from a crash would keep its old mode
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (secret && !OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnly;
            using (var fs = new FileStream(tmp, options))
            {
                fs.Write(Encoding.UTF8.GetBytes(content));
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, full, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>
    /// Moves a file that can't be read aside (<c>state.json.corrupt-20261113T200102Z</c>), so it isn't overwritten
    /// and can be looked at later. Returns the new name, or null if it couldn't be moved.
    /// </summary>
    public static string? Quarantine(string path, TimeProvider? time = null)
    {
        try
        {
            var stamp = (time ?? TimeProvider.System).GetUtcNow().ToString("yyyyMMdd'T'HHmmss'Z'");
            var target = $"{path}.corrupt-{stamp}";
            File.Move(path, target, overwrite: true);
            return target;
        }
        catch (Exception) { return null; }
    }
}
