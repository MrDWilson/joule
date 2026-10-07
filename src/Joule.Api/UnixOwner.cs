using System.Runtime.InteropServices;

namespace Joule;

/// <summary>The result of <see cref="ConfigFileArchive.ReplaceFile"/>: the snapshot taken first, the version written, and both hashes.</summary>
public sealed record FileReplaceResult(ConfigFileVersion Before, ConfigFileVersion After, string BeforeHash, string AfterHash);

/// <summary>
/// A file's owner (user and group ids) on Linux and macOS, so a file Joule rewrites keeps the owner it had. .NET exposes permissions
/// but not ownership, so this reads stat(2) directly for the platforms whose layout is known (Linux x64 and arm64, macOS arm64) and
/// returns null elsewhere; Joule then rewrites the file in place, which never changes its owner.
/// </summary>
static class UnixOwner
{
    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    static extern int Stat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer);
    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    static extern int Chown([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint uid, uint gid);
    [DllImport("libc", EntryPoint = "geteuid")]
    static extern uint GetEuid();

    static (int Uid, int Gid)? Offsets() => (OperatingSystem.IsLinux(), OperatingSystem.IsMacOS(), RuntimeInformation.ProcessArchitecture) switch
    {
        (true, _, Architecture.X64) => (28, 32),
        (true, _, Architecture.Arm64) => (24, 28),
        (_, true, Architecture.Arm64) => (16, 20),
        _ => null,
    };

    public static (uint Uid, uint Gid)? Get(string path)
    {
        if (Offsets() is not { } at) return null;
        try
        {
            var buffer = new byte[512];
            if (Stat(path, buffer) != 0) return null;
            return (BitConverter.ToUInt32(buffer, at.Uid), BitConverter.ToUInt32(buffer, at.Gid));
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    public static bool Set(string path, uint uid, uint gid)
    {
        if (Offsets() is null) return false;
        try { return Chown(path, uid, gid) == 0; }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    /// <summary>The user Joule runs as, or null where it can't be read.</summary>
    public static uint? CurrentUser()
    {
        if (Offsets() is null) return null;
        try { return GetEuid(); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }
}
