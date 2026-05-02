using System.Runtime.InteropServices;

namespace dapps.core.Updater;

/// <summary>Real-filesystem implementation of
/// <see cref="IUpdaterFileSystem"/>. Used by the
/// <c>dapps --apply-update</c> / <c>--rollback</c> entry points.</summary>
public sealed class RealUpdaterFileSystem : IUpdaterFileSystem
{
    public bool Exists(string path) => File.Exists(path);

    public void SwapInPlace(string src, string dest, string previous)
    {
        // Park the existing dest as `.previous` (overwrites any stale
        // copy left by an earlier update), then move the staged new
        // binary into place. Both renames are intra-directory so they
        // hit the rename(2) atomic-replace semantics on POSIX. If the
        // first rename fails, no swap has happened yet; if the second
        // fails, we've lost the live binary — caller's responsibility
        // to roll back from `.previous`.
        if (File.Exists(previous)) File.Delete(previous);
        if (File.Exists(dest)) File.Move(dest, previous);
        File.Move(src, dest);
    }

    public void Restore(string previous, string dest)
    {
        if (!File.Exists(previous))
        {
            throw new FileNotFoundException(
                $"Cannot restore: no previous binary at {previous}", previous);
        }
        if (File.Exists(dest)) File.Delete(dest);
        File.Move(previous, dest);
    }

    public void MarkExecutable(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            && !RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return;
        // .NET 7+ ships UnixFileMode on File. 0755 = rwxr-xr-x.
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    public string? ReadAllText(string path)
        => File.Exists(path) ? File.ReadAllText(path) : null;

    public void WriteAllText(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, contents);
        // Status / request files need to be readable by the unprivileged
        // dapps user (which writes the request, reads the status). Mode
        // 0644 = rw-r--r--; root writes, dapps reads.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            try
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite
                    | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
            catch { /* best-effort; dashboard read can still work via group=dapps */ }
        }
    }

    public void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* idempotent — caller doesn't care about absent files */ }
    }
}
