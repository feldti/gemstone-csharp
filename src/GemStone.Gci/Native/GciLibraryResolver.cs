using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace GemStone.Gci.Native;

/// <summary>
/// Resolves the logical library name <see cref="GciNative.LibraryName"/> to the versioned
/// GemBuilder RPC library shipped with GemStone, e.g. <c>libgcirpc-3.7.5-64.so</c> (Linux),
/// <c>libgcirpc-3.7.5-64.dylib</c> (macOS) or <c>libgcirpc-3.7.5-64.dll</c> (Windows client).
/// </summary>
/// <remarks>
/// Search order:
/// <list type="number">
///   <item><c>GEMSTONE_GCI_LIBRARY</c> — full path to the library file.</item>
///   <item><c>$GEMSTONE/lib</c> and <c>$GEMSTONE/bin</c> (Windows client installs keep the DLLs in <c>bin</c>).</item>
///   <item>The application directory — per chapter 3 of the GCI manual an application may run
///         without <c>GEMSTONE</c> if the GemStone libraries sit next to the executable.</item>
///   <item>The normal OS loader search (<c>LD_LIBRARY_PATH</c> / <c>PATH</c>) for <c>libgcirpc</c>.</item>
/// </list>
/// Within one directory the highest version wins. Loading by absolute path lets the OS find
/// the library's own dependencies (e.g. the OpenSSL DLLs on Windows) in the same directory.
/// </remarks>
internal static partial class GciLibraryResolver
{
    public const string LibraryPathVariable = "GEMSTONE_GCI_LIBRARY";
    public const string GemStoneVariable = "GEMSTONE";
    public const string BaseName = "libgcirpc";

#pragma warning disable CA2255 // ModuleInitializer is intended here: the resolver must be registered before the first P/Invoke.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Register() =>
        NativeLibrary.SetDllImportResolver(typeof(GciLibraryResolver).Assembly, Resolve);

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != GciNative.LibraryName)
            return IntPtr.Zero;

        foreach (var candidate in Candidates(Environment.GetEnvironmentVariable, AppContext.BaseDirectory, CurrentExtension))
        {
            if (NativeLibrary.TryLoad(candidate, out var handle))
                return handle;
        }

        // Let the runtime report its standard DllNotFoundException.
        return IntPtr.Zero;
    }

    /// <summary>File extension of shared libraries on the current OS.</summary>
    internal static string CurrentExtension =>
        OperatingSystem.IsWindows() ? ".dll" : OperatingSystem.IsMacOS() ? ".dylib" : ".so";

    /// <summary>Library paths/names to try, in order. Pure function of its inputs, for tests.</summary>
    internal static IEnumerable<string> Candidates(Func<string, string?> getEnv, string? appDirectory, string extension)
    {
        var explicitPath = getEnv(LibraryPathVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath))
            yield return explicitPath;

        var gemstone = getEnv(GemStoneVariable);
        if (!string.IsNullOrWhiteSpace(gemstone))
        {
            foreach (var file in VersionedLibraries(Path.Combine(gemstone, "lib"), extension))
                yield return file;
            foreach (var file in VersionedLibraries(Path.Combine(gemstone, "bin"), extension))
                yield return file;
        }

        if (!string.IsNullOrWhiteSpace(appDirectory))
            foreach (var file in VersionedLibraries(appDirectory, extension))
                yield return file;

        yield return BaseName;
    }

    /// <summary><c>libgcirpc-&lt;version&gt;-64&lt;ext&gt;</c> files in a directory, highest version first.</summary>
    private static IEnumerable<string> VersionedLibraries(string directory, string extension)
    {
        if (!Directory.Exists(directory))
            return [];

        return Directory.EnumerateFiles(directory, $"{BaseName}-*{extension}")
            .Select(path => (path, version: ParseVersion(Path.GetFileName(path))))
            .OrderByDescending(x => x.version)
            .ThenByDescending(x => x.path, StringComparer.Ordinal)
            .Select(x => x.path);
    }

    private static Version ParseVersion(string fileName)
    {
        var match = VersionPattern().Match(fileName);
        return match.Success && Version.TryParse(match.Groups[1].Value, out var v) ? v : new Version(0, 0);
    }

    [GeneratedRegex(@"^libgcirpc-(\d+(?:\.\d+){1,3})", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
