using GemStone.Gci.Native;

namespace GemStone.Gci.Tests;

public class GciLibraryResolverTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("gs-resolver-");

    public void Dispose() => _root.Delete(recursive: true);

    private string Touch(string relativeDir, string fileName)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root.FullName, relativeDir));
        var path = Path.Combine(dir.FullName, fileName);
        File.WriteAllText(path, "");
        return path;
    }

    [Theory]
    [InlineData(".so")]
    [InlineData(".dylib")]
    [InlineData(".dll")]
    public void SearchOrderIsExplicitThenGemstoneLibBinThenAppDirThenDefault(string ext)
    {
        var lib = Touch("gs/lib", $"libgcirpc-3.7.5-64{ext}");
        var bin = Touch("gs/bin", $"libgcirpc-3.7.5-64{ext}");
        var app = Touch("app", $"libgcirpc-3.7.5-64{ext}");
        Touch("gs/lib", "libgcirpc-3.7.5-64.other"); // wrong extension: ignored

        var env = new Dictionary<string, string?>
        {
            [GciLibraryResolver.LibraryPathVariable] = "/opt/custom/libgcirpc.x",
            [GciLibraryResolver.GemStoneVariable] = Path.Combine(_root.FullName, "gs"),
        };

        var candidates = GciLibraryResolver
            .Candidates(k => env.GetValueOrDefault(k), Path.Combine(_root.FullName, "app"), ext)
            .ToList();

        Assert.Equal(["/opt/custom/libgcirpc.x", lib, bin, app, "libgcirpc"], candidates);
    }

    [Fact]
    public void HighestVersionWinsNumericallyNotLexically()
    {
        var v9 = Touch("gs/lib", "libgcirpc-3.7.9-64.so");
        var v10 = Touch("gs/lib", "libgcirpc-3.7.10-64.so");
        var v5 = Touch("gs/lib", "libgcirpc-3.7.5-64.so");

        var candidates = GciLibraryResolver
            .Candidates(k => k == GciLibraryResolver.GemStoneVariable ? Path.Combine(_root.FullName, "gs") : null, null, ".so")
            .ToList();

        Assert.Equal([v10, v9, v5, "libgcirpc"], candidates);
    }

    [Fact]
    public void FallsBackToOsSearchWithoutEnvironment() =>
        Assert.Equal(["libgcirpc"], GciLibraryResolver.Candidates(_ => null, null, ".so"));

    [Fact]
    public void CurrentExtensionMatchesPlatform()
    {
        var expected = OperatingSystem.IsWindows() ? ".dll" : OperatingSystem.IsMacOS() ? ".dylib" : ".so";
        Assert.Equal(expected, GciLibraryResolver.CurrentExtension);
    }
}
