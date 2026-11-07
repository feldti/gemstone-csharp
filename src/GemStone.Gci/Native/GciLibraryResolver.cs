using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace GemStone.Gci.Native;

/// <summary>
/// Finds the versioned GemBuilder RPC library shipped with GemStone (loaded by <see cref="GciLibrary"/>), e.g. <c>libgcirpc-3.7.5-64.so</c> (Linux),
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

    /// <summary>File extension of shared libraries on the current OS.</summary>
    internal static string CurrentExtension =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".dll"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? ".dylib" : ".so";

    /// <summary>Library paths/names to try, in order. Pure function of its inputs, for tests.</summary>
    internal static IEnumerable<string> Candidates(Func<string, string?> getEnv, string? appDirectory, string extension)
    {
        var explicitPath = getEnv(LibraryPathVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath))
            yield return explicitPath!;

        var gemstone = getEnv(GemStoneVariable);
        if (!string.IsNullOrWhiteSpace(gemstone))
        {
            foreach (var file in VersionedLibraries(Path.Combine(gemstone!, "lib"), extension))
                yield return file;
            foreach (var file in VersionedLibraries(Path.Combine(gemstone, "bin"), extension))
                yield return file;
        }

        if (!string.IsNullOrWhiteSpace(appDirectory))
            foreach (var file in VersionedLibraries(appDirectory!, extension))
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

    private const string VersionRegex = @"^libgcirpc-(\d+(?:\.\d+){1,3})";
#if NET7_0_OR_GREATER
    [GeneratedRegex(VersionRegex, RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
#else
    private static readonly Regex VersionRegexInstance = new(VersionRegex, RegexOptions.CultureInvariant);
    private static Regex VersionPattern() => VersionRegexInstance;
#endif
}
