// gsrepl — a minimal interactive Smalltalk console on top of GemStone.Gci (Linux and Windows).
//
//   dotnet run --project samples/GsRepl -- [--stone NAME] [--gem-service NRS] [--user U]
//                                          [--password P] [--host-user U] [--host-password P]
//                                          [--eval "expr"]
//
// Every option can also come from an environment variable (GS_STONE, GS_GEM_SERVICE, GS_USER,
// GS_PASSWORD, GS_HOST_USER, GS_HOST_PASSWORD). Without a password you are prompted.
// GEMSTONE (or GEMSTONE_GCI_LIBRARY) must point to the GemStone installation, or the GemStone
// client libraries must sit next to the executable.

using System.Text;
using GemStone.Gci;

// The Windows console defaults to an OEM code page (e.g. 850); use UTF-8 so non-ASCII
// Smalltalk source and results survive. Harmless elsewhere.
if (OperatingSystem.IsWindows())
{
    Console.InputEncoding = Encoding.UTF8;
    Console.OutputEncoding = Encoding.UTF8;
}

var options = Options.Parse(args);
if (options is null)
{
    Options.PrintUsage();
    return 2;
}

GciSession session;
try
{
    Console.Error.WriteLine($"Login {options.User}@{options.Stone} via {options.GemService} …");
    session = GciSession.Login(
        new GciNetParameters(options.Stone, options.HostUser, options.HostPassword, options.GemService),
        options.User,
        options.Password ?? ReadPassword($"Password for {options.User}: "));
}
catch (Exception ex) when (ex is GciException or DllNotFoundException or PlatformNotSupportedException)
{
    Console.Error.WriteLine($"Login failed: {ex.Message}");
    if (ex is DllNotFoundException)
        Console.Error.WriteLine(OperatingSystem.IsWindows()
            ? "Set GEMSTONE to the GemStone client directory (DLLs in %GEMSTONE%\\bin), set GEMSTONE_GCI_LIBRARY to libgcirpc-*.dll, or copy the GemStone DLLs next to gsrepl.exe."
            : "Set GEMSTONE to the GemStone installation, set GEMSTONE_GCI_LIBRARY to libgcirpc-*.so, or copy the GemStone libraries next to gsrepl.");
    return 1;
}

using (session)
{
    if (options.Eval is not null)
        return Evaluate(session, options.Eval) ? 0 : 1;

    var executing = false;
    Console.CancelKeyPress += (_, e) =>
    {
        if (!Volatile.Read(ref executing)) return; // at the prompt: Ctrl+C quits as usual
        e.Cancel = true;
        session.SoftBreak();
    };

    Console.WriteLine("Connected. Enter Smalltalk, end a line with '\\' to continue it. :help for commands.");
    while (true)
    {
        var source = ReadStatement();
        if (source is null) break; // EOF
        if (source.Length == 0) continue;

        switch (source)
        {
            case ":q" or ":quit" or ":exit":
                return 0;
            case ":help":
                Console.WriteLine("""
                      <Smalltalk>   execute and print the result's printString
                      :commit       commit the transaction (conflicts are reported)
                      :abort        discard uncommitted changes
                      :q            log out and quit
                      Ctrl+C        while code runs: soft break (interrupt)
                    """);
                continue;
            case ":commit":
                RunCommand(session, s =>
                {
                    s.Commit();
                    Console.WriteLine("Committed.");
                });
                continue;
            case ":abort":
                RunCommand(session, s =>
                {
                    s.Abort();
                    Console.WriteLine("Aborted.");
                });
                continue;
        }

        Volatile.Write(ref executing, true);
        try
        {
            if (!Evaluate(session, source) && !session.IsLoggedIn)
            {
                Console.Error.WriteLine("Session lost — exiting.");
                return 1;
            }
        }
        finally
        {
            Volatile.Write(ref executing, false);
        }
    }
    return 0;
}

// Executes source, prints "=> printString" or the error. Returns false on error.
static bool Evaluate(GciSession session, string source)
{
    try
    {
        var result = session.Execute(source);
        try
        {
            Console.WriteLine($"=> {session.PrintString(result)}");
        }
        finally
        {
            session.Release(result);
        }
        return true;
    }
    catch (GciException ex)
    {
        Console.Error.WriteLine($"Error {ex.Number}{(ex.IsFatal ? " (fatal)" : "")}: {ex.Message.Split(": ", 2)[^1]}");
        if (!string.IsNullOrEmpty(ex.Reason))
            Console.Error.WriteLine($"  reason: {ex.Reason}");
        return false;
    }
}

static void RunCommand(GciSession session, Action<GciSession> command)
{
    try
    {
        command(session);
    }
    catch (GciTransactionConflictException ex)
    {
        Console.Error.WriteLine("Commit failed: transaction conflict. Use :abort, then redo the work.");
        if (ex.ConflictReport is not null)
            Console.Error.WriteLine($"  {ex.ConflictReport}");
    }
    catch (GciException ex)
    {
        Console.Error.WriteLine($"Error {ex.Number}: {ex.Message}");
    }
}

// Reads one statement; lines ending in '\' are joined with the next line.
static string? ReadStatement()
{
    var parts = new List<string>();
    Console.Write("gs> ");
    while (true)
    {
        var line = Console.ReadLine();
        if (line is null) return parts.Count == 0 ? null : string.Join('\n', parts);
        if (line.EndsWith('\\'))
        {
            parts.Add(line[..^1]);
            Console.Write("..> ");
            continue;
        }
        parts.Add(line);
        return string.Join('\n', parts).Trim();
    }
}

static string ReadPassword(string prompt)
{
    Console.Error.Write(prompt);
    if (Console.IsInputRedirected)
        return Console.ReadLine() ?? string.Empty;

    var chars = new List<char>();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); }
        else if (!char.IsControl(key.KeyChar)) chars.Add(key.KeyChar);
    }
    Console.Error.WriteLine();
    return new string(chars.ToArray());
}

internal sealed record Options(
    string Stone, string GemService, string User, string? Password,
    string HostUser, string HostPassword, string? Eval)
{
    public static Options? Parse(string[] args)
    {
        string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) ?? fallback;

        var o = new Options(
            Env("GS_STONE", "gs64stone"),
            Env("GS_GEM_SERVICE", "gemnetobject"),
            Env("GS_USER", "DataCurator"),
            Environment.GetEnvironmentVariable("GS_PASSWORD"),
            Env("GS_HOST_USER", ""),
            Env("GS_HOST_PASSWORD", ""),
            null);

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "-h" or "--help") return null;
            if (i + 1 >= args.Length) return null;
            var value = args[++i];
            o = args[i - 1] switch
            {
                "--stone" => o with { Stone = value },
                "--gem-service" => o with { GemService = value },
                "--user" => o with { User = value },
                "--password" => o with { Password = value },
                "--host-user" => o with { HostUser = value },
                "--host-password" => o with { HostPassword = value },
                "--eval" or "-e" => o with { Eval = value },
                _ => null!,
            };
            if (o is null) return null;
        }
        return o;
    }

    public static void PrintUsage() => Console.Error.WriteLine("""
        Usage: gsrepl [--stone NAME] [--gem-service NRS] [--user U] [--password P]
                      [--host-user U] [--host-password P] [--eval "expr"]
        Defaults / env: GS_STONE=gs64stone, GS_GEM_SERVICE=gemnetobject, GS_USER=DataCurator,
                        GS_PASSWORD (prompted if unset), GS_HOST_USER, GS_HOST_PASSWORD.
        GEMSTONE or GEMSTONE_GCI_LIBRARY must locate libgcirpc (.so / .dll), or the GemStone
        client libraries must sit next to the executable.
        """);
}
