using System.Reflection;
using System.Runtime.InteropServices;
using GemStone.Gci.Native;

namespace GemStone.Gci.Tests;

/// <summary>
/// Every [LibraryImport] in GciNative must name a symbol the GemStone library really exports —
/// catches typos and header macros (e.g. GciStoreBytes is a macro for GciStoreBytesInstanceOf)
/// before the first call fails at runtime. Needs only the library, no stone.
/// </summary>
public class GciNativeExportTests
{
    [GciLibraryFact]
    public void AllLibraryImportsAreExported()
    {
        var handle = GciLibraryFactAttribute.LoadLibrary()!.Value;
        var missing = typeof(GciNative)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(m => (method: m, import: m.GetCustomAttribute<LibraryImportAttribute>()))
            .Where(x => x.import is not null)
            .Select(x => x.import!.EntryPoint ?? x.method.Name)
            .Where(name => !NativeLibrary.TryGetExport(handle, name, out _))
            .ToList();

        Assert.Empty(missing);
    }
}

/// <summary>Skips unless the GemStone GCI library can be located (GEMSTONE / GEMSTONE_GCI_LIBRARY).</summary>
public sealed class GciLibraryFactAttribute : FactAttribute
{
    public GciLibraryFactAttribute()
    {
        if (LoadLibrary() is null)
            Skip = "GemStone GCI library not found — set GEMSTONE or GEMSTONE_GCI_LIBRARY";
    }

    public static IntPtr? LoadLibrary()
    {
        foreach (var candidate in GciLibraryResolver.Candidates(
                     Environment.GetEnvironmentVariable, AppContext.BaseDirectory, GciLibraryResolver.CurrentExtension))
            if (NativeLibrary.TryLoad(candidate, out var handle))
                return handle;
        return null;
    }
}
