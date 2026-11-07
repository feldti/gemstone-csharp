namespace GemStone.Gci.Tests;

/// <summary>
/// Connection settings for tests against a real stone. Set GS_TEST_STONE (plus optionally
/// GS_TEST_USER, GS_TEST_PASSWORD, GS_TEST_HOST_USER, GS_TEST_HOST_PASSWORD,
/// GS_TEST_GEM_SERVICE) and GEMSTONE (or GEMSTONE_GCI_LIBRARY) to enable.
/// </summary>
internal static class StoneEnv
{
    private static string Env(string name, string fallback = "") =>
        Environment.GetEnvironmentVariable(name) ?? fallback;

    public static GciNetParameters Net => new(
        Env("GS_TEST_STONE"),
        Env("GS_TEST_HOST_USER"),
        Env("GS_TEST_HOST_PASSWORD"),
        Env("GS_TEST_GEM_SERVICE", "gemnetobject"));

    public static string User => Env("GS_TEST_USER", "DataCurator");
    public static string Password => Env("GS_TEST_PASSWORD", "swordfish");

    public static GciSession Login() => GciSession.Login(Net, User, Password);
}

public sealed class StoneFactAttribute : FactAttribute
{
    public StoneFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GS_TEST_STONE")))
            Skip = "GS_TEST_STONE not set — no GemStone stone available";
    }
}

public sealed class StoneTheoryAttribute : TheoryAttribute
{
    public StoneTheoryAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GS_TEST_STONE")))
            Skip = "GS_TEST_STONE not set — no GemStone stone available";
    }
}


// The classic GCI has one current session per process: keep stone tests out of parallel runs.
[CollectionDefinition("Stone", DisableParallelization = true)]
public sealed class StoneCollection { }

[Collection("Stone")]
public class GciLoginIntegrationTests
{
    [StoneFact]
    public void LoginAndLogoutSucceed()
    {
        using var session = StoneEnv.Login();
    }

    [StoneFact]
    public void WrongPasswordThrowsGciException()
    {
        var ex = Assert.Throws<GciException>(() =>
            GciSession.Login(StoneEnv.Net, StoneEnv.User, "definitely-wrong"));
        Assert.NotEqual(0, ex.Number);
    }
}
