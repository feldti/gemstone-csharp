using System.Runtime.InteropServices;

namespace GemStone.Gci.Native;

/// <summary>
/// Loads the GCI library once (by path, from <see cref="GciLibraryResolver.Candidates"/>) and
/// resolves its exports. .NET uses <c>NativeLibrary</c>; the net48 build uses
/// <c>LoadLibraryExW</c>/<c>GetProcAddress</c> on Windows (.NET Framework) and <c>dlopen</c>/<c>dlsym</c>
/// elsewhere (Mono on Linux, used to run the test suite against the net48 build).
/// </summary>
internal static class GciLibrary
{
    private static readonly object Gate = new();
    private static nint _handle;
    private static string? _path;

    /// <summary>Path (or name) the library was loaded from; <c>null</c> before the first GCI call.</summary>
    public static string? LoadedFrom => _path;

    private static nint Handle
    {
        get
        {
            if (_handle != 0) return _handle;
            lock (Gate)
            {
                if (_handle == 0) _handle = Load();
                return _handle;
            }
        }
    }

    public static nint GetExport(string name) =>
        TryGetExport(name, out var address)
            ? address
            : throw new EntryPointNotFoundException($"'{name}' is not exported by the GemStone GCI library {_path}.");

    public static bool TryGetExport(string name, out nint address)
    {
#if NET
        return NativeLibrary.TryGetExport(Handle, name, out address);
#else
        address = IsWindows ? GetProcAddress(Handle, name) : dlsym(Handle, name);
        return address != 0;
#endif
    }

    private static nint Load()
    {
        var tried = new List<string>();
        foreach (var candidate in GciLibraryResolver.Candidates(Environment.GetEnvironmentVariable, AppContext.BaseDirectory, GciLibraryResolver.CurrentExtension))
        {
            if (TryLoad(candidate, out var handle))
            {
                _path = candidate;
                return handle;
            }
            tried.Add(candidate);
        }
        throw new DllNotFoundException(
            "GemStone GCI library (libgcirpc-<version>-64) not found. Set GEMSTONE to the GemStone installation, " +
            $"or GEMSTONE_GCI_LIBRARY to the library file. Tried: {string.Join(", ", tried)}");
    }

    private static bool TryLoad(string candidate, out nint handle)
    {
#if NET
        return NativeLibrary.TryLoad(candidate, out handle);
#else
        if (!IsWindows)
        {
            handle = dlopen(candidate.Contains('/') ? candidate : candidate + GciLibraryResolver.CurrentExtension, RtldNow);
            return handle != 0;
        }
        // Absolute path: LOAD_WITH_ALTERED_SEARCH_PATH lets Windows find the library's own
        // dependencies (OpenSSL, Kerberos DLLs) in the library's directory.
        handle = Path.IsPathRooted(candidate)
            ? LoadLibraryExW(candidate, 0, LoadWithAlteredSearchPath)
            : LoadLibraryExW(candidate + ".dll", 0, 0);
        return handle != 0;
#endif
    }

#if !NET
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private const uint LoadWithAlteredSearchPath = 0x00000008;
    private const int RtldNow = 2;

    [DllImport("libdl.so.2", CharSet = CharSet.Ansi)]
    private static extern nint dlopen(string fileName, int flags);

    [DllImport("libdl.so.2", CharSet = CharSet.Ansi)]
    private static extern nint dlsym(nint handle, string symbol);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryExW(string lpLibFileName, nint hFile, uint dwFlags);

    [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    private static extern nint GetProcAddress(nint hModule, string procName);
#endif
}
