using System.Diagnostics;
using System.Reflection;
using Xunit;
using Xunit.Sdk;

// Runs every [Fact]/[Theory] (incl. derived attributes like [StoneFact]) of the test assembly:
// one instance per test, IClassFixture<T> created once per class, InlineData/MemberData, async
// tests, Skip. Usage: mono Net48Runner.exe [filter-substring]
var filter = args.Length > 0 ? args[0] : null;
var assembly = typeof(GemStone.Gci.Tests.OopTests).Assembly;
int passed = 0, failed = 0, skipped = 0;
var failures = new List<string>();
var clock = Stopwatch.StartNew();

foreach (var type in assembly.GetTypes().Where(t => t.IsPublic && t.IsClass && !t.IsAbstract).OrderBy(t => t.FullName))
{
    var tests = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Select(m => (method: m, fact: m.GetCustomAttribute<FactAttribute>()))
        .Where(x => x.fact is not null && (filter is null || $"{type.FullName}.{x.method.Name}".Contains(filter)))
        .ToList();
    if (tests.Count == 0) continue;

    var fixtures = new Dictionary<Type, object>();
    try
    {
        foreach (var (method, fact) in tests)
        {
            var name = $"{type.FullName}.{method.Name}";
            if (fact!.Skip is not null)
            {
                skipped++;
                continue;
            }
            var cases = fact is TheoryAttribute
                ? method.GetCustomAttributes<DataAttribute>().SelectMany(d => d.GetData(method)).ToList()
                : [Array.Empty<object?>()];
            foreach (var data in cases)
            {
                var label = data.Length == 0 ? name : $"{name}({string.Join(", ", data.Select(d => d?.ToString() ?? "null"))})";
                try
                {
                    var instance = Create(type, fixtures);
                    try
                    {
                        var parameters = method.GetParameters();
                        var callArgs = data.Select((d, i) => Convert(d, parameters[i].ParameterType)).ToArray();
                        var result = method.Invoke(instance, callArgs);
                        if (result is Task task) task.GetAwaiter().GetResult();
                    }
                    finally
                    {
                        (instance as IDisposable)?.Dispose();
                    }
                    passed++;
                }
                catch (Exception e)
                {
                    failed++;
                    var inner = e is TargetInvocationException { InnerException: { } ie } ? ie : e;
                    failures.Add($"FAIL {label}{Environment.NewLine}     {inner.GetType().Name}: {inner.Message}{Environment.NewLine}{inner.StackTrace}");
                    Console.WriteLine($"FAIL {label}");
                }
            }
        }
    }
    finally
    {
        foreach (var f in fixtures.Values) (f as IDisposable)?.Dispose();
    }
}

foreach (var f in failures) Console.WriteLine(f + Environment.NewLine);
Console.WriteLine($"net48 ({(Type.GetType("Mono.Runtime") is null ? ".NET Framework" : "Mono")}): passed {passed}, failed {failed}, skipped {skipped}, {clock.Elapsed.TotalSeconds:F1} s");
return failed == 0 ? 0 : 1;

static object Create(Type type, Dictionary<Type, object> fixtures)
{
    var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
    var ctorArgs = ctor.GetParameters().Select(p =>
    {
        if (!fixtures.TryGetValue(p.ParameterType, out var fixture))
            fixtures[p.ParameterType] = fixture = Activator.CreateInstance(p.ParameterType)!;
        return fixture;
    }).ToArray();
    return ctor.Invoke(ctorArgs);
}

static object? Convert(object? value, Type target)
{
    if (value is null || target.IsInstanceOfType(value)) return value;
    var t = Nullable.GetUnderlyingType(target) ?? target;
    return t.IsEnum ? Enum.ToObject(t, value) : System.Convert.ChangeType(value, t, System.Globalization.CultureInfo.InvariantCulture);
}
