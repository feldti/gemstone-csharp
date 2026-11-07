using System.Runtime.InteropServices;
using GemStone.Gci.Native;

namespace GemStone.Gci.Tests;

/// <summary>
/// Every entry point of GciNative must name a symbol the GemStone library really exports —
/// catches typos and header macros (e.g. GciStoreBytes is a macro for GciStoreBytesInstanceOf)
/// before the first call fails at runtime. Needs only the library, no stone.
/// </summary>
public class GciNativeExportTests
{
    [GciLibraryFact]
    public void AllEntryPointsAreExported()
    {
        var names = GciNative.EntryPointNames().ToList();
        Assert.True(names.Count >= 60, $"only {names.Count} entry points found");
        var missing = names.Where(name => !GciLibrary.TryGetExport(name, out _)).ToList();
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
        try
        {
            return GciLibrary.TryGetExport("GciInit", out var address) ? address : null;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
    }
}
