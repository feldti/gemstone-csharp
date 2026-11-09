# gemstone-csharp

**🚧 Work in progress: not ready for production use. 🚧**

A .NET 10, .NET 8 and .NET Framework 4.8 wrapper for **GemStone/S 64 Bit 3.6 and 3.7**, built on the classic (non-thread-safe)
**GemBuilder for C** (GCI, `libgcirpc`). It lets C# programs log in to a stone, run Smalltalk,
convert values, run transactions, read and write objects, and load object graphs into C# classes.

> [!WARNING]
> **Work in progress.** This project is under active development and not ready for production
> use. APIs may change without notice, there are no releases or NuGet packages yet, and some
> parts are missing (see [Not implemented yet](#not-implemented-yet)).
>
> What exists is covered by integration tests against GemStone 3.6.5, 3.6.8, 3.7.2, 3.7.5 and 3.7.6 on Linux x86-64,
> for .NET 10 and .NET 8, and on a Windows x64 client (GemStone 3.7.6 client against a Linux
> stone) for .NET 10, .NET 8 and .NET Framework 4.8. Linux arm64 and macOS on Apple Silicon are
> supported with .NET 10 only and have not been tested yet; see [docs/platform-testing.md](docs/platform-testing.md).

## Features

- **Sessions:** login via NetLDI (`GciSetNet` + `GciLoginEx`), several sessions in one process,
  quiet login (no `gci login:` banner on stdout).
- **Execution:** `Execute` / `Perform` / `Evaluate<T>` / `Send<T>`. Non-ASCII source text works
  (it is sent as a `Utf8` object). GemStone errors become `GciException`; the error stack is
  cleared and the error objects are released.
- **Value conversion** between GemStone and .NET:

  | GemStone | .NET |
  |---|---|
  | `nil` | `null` |
  | Boolean | `bool` |
  | SmallInteger / LargeInteger | `long` / `BigInteger` |
  | Float | `double` |
  | ScaledDecimal | `decimal` |
  | Character | `Rune` |
  | all String / Symbol classes | `string` |
  | ByteArray | `byte[]` |
  | DateAndTime | `DateTimeOffset` |
  | Date | `DateOnly` |
  | Time | `TimeOnly` |

  Immediate values (SmallInteger, SmallDouble, Character, Boolean) are decoded locally, without
  a round trip. `ToValues` converts many objects in one round trip.
- **Transactions:** `Commit` / `TryCommit` / `Abort` / `Begin`, transaction modes, conflict
  detection (`GciTransactionConflictException`), and `RunInTransaction` with retry.
- **Structural object access:** named and indexed instance variables, bytes, NSCs, `NewObject`,
  `ResolveSymbol`.
- **Async:** `ExecuteAsync`, `PerformAsync`, `CommitAsync`, … on top of the nonblocking GCI
  (`GciNb*`). Cancellation sends a soft break.
- **Traversals:** load a whole object graph in one round trip (`Traverse`, `ExecuteTraverse`,
  `PerformTraverse`) and decode it offline. `FetchPaths` reads instance-variable paths from many
  objects at once.
- **Notifications:** `GciSignalListener` reports changes other sessions commit to watched objects
  and signals sent with `SendSignal` (or `System sendSignal:to:withMessage:`), by polling on a
  session of its own, optionally in the background with an event. See
  [docs/programming-model.md](docs/programming-model.md#notifications-from-the-server).
- **Object mapping:** load GemStone objects into your own C# classes, mapped with attributes,
  and write changes back with `SaveChanges`. Instance-variable names may differ from property
  names. Shared references, cycles, subclasses and collections are handled, and mapping errors
  are reported with suggestions.

## Requirements

- .NET SDK 10.0 to build. The library targets **.NET 10**, **.NET 8** (LTS) and **.NET Framework 4.8**;
  the latter is for existing Windows applications (WinForms, WPF), which must run as **x64** processes.
- The GemStone/S 64 Bit 3.6.x or 3.7.x client library (`libgcirpc-3.x.y-64.so`, or `.dll` on Windows),
  usually taken from a GemStone installation (`$GEMSTONE`)
- A running stone and NetLDI to connect to
- 64-bit process only: Linux x86-64; Windows x64 as a client; and with **.NET 10 only**, Linux
  arm64 (e.g. a Raspberry Pi with a 64-bit OS) and macOS on Apple Silicon (GemStone 3.7 and later;
  GemTalk supports both for development only)

## Finding the GCI library

The wrapper looks for the versioned client library in this order; the highest version wins:

1. `GEMSTONE_GCI_LIBRARY`: full path to the library file
2. `$GEMSTONE/lib` (Linux), then `$GEMSTONE/bin` (Windows client)
3. the application directory
4. the normal OS library search

On Linux, `libgcirpc` loads `libssl-*.so` and `libkrb5-*.so` at runtime. Ship them next to your
binary if you run without `$GEMSTONE`.

### .NET Framework 4.8

The `net48` build has the same API. Differences:
- `DateOnly` / `TimeOnly` come from the `Portable.System.DateTimeOnly` package.
- `System.Text.Rune` is a small stand-in type shipped with the library.
- The application must not run as a 32-bit process: set the platform target to `x64`, or
  `AnyCPU` with "Prefer 32-bit" off.

## Quick start

How the pieces fit together (sessions, the object mapper, transactions, persistence by
reachability) is described in [docs/programming-model.md](docs/programming-model.md).

```csharp
using GemStone.Gci;

var net = new GciNetParameters(
    StoneName: "gs64stone",
    GemService: "!@localhost#netldi:gs64ldi!gemnetobject");

using var session = GciSession.Login(net, "DataCurator", "swordfish");

long sum = session.Evaluate<long>("3 + 4");                  // 7
string text = session.Evaluate<string>("'Grüße' , ' aus GemStone'");
decimal price = session.Evaluate<decimal>("12.34s2");

session.RunInTransaction(s =>
{
    s.Evaluate("UserGlobals at: #Counter put: (UserGlobals at: #Counter ifAbsent: [0]) + 1");
});

int size = await session.EvaluateAsync<int>("(1 to: 1000000) inject: 0 into: [:a :b | a + 1]");
```

### Loading objects into C# classes

```csharp
using GemStone.Gci.Mapping;

[GsClass("CustomerAccount")]
public class Account
{
    [GsOop] public Oop Oop { get; private set; }
    [GsInstVar("accName")] public string? Name { get; set; }
    [GsInstVar("amount")] public decimal Balance { get; set; }
    [GsInstVar("postalAddress")] public Address? Address { get; set; }
    [GsInstVar("orderList"), GsCollection(GsCollectionKind.Ordered)] public List<Order> Orders { get; set; } = [];
    [GsInstVar("state")] public AccountStatus Status { get; set; }   // Symbol #active → enum
}

using var ctx = new GsObjectContext(session);
ctx.Validate(typeof(Account));   // check the mapping against the image
var accounts = ctx.Load<List<Account>>("UserGlobals at: #Accounts");

accounts[0].Balance += 100m;
accounts[0].Address = new Address { City = "Köln" };   // new object
accounts[0].Orders.Add(new Order { Number = 42 });     // added to the existing collection
ctx.SaveChanges();                                     // writes only what changed
session.Commit();                                      // or both at once: ctx.SaveChangesAndCommit()
```

How `SaveChanges` works:
- **Change tracking:** the context remembers the loaded state and keeps the loaded objects in
  the export set until it is disposed. Turn this off for pure reading with
  `GsLoadOptions { TrackChanges = false }`.
- **What it writes:** only properties and collections whose contents changed. New C# objects
  are created with `new` on their GemStone class. Strings and enums that were Symbols are
  written back as Symbols.
- **Loading is batched:** values a traversal cannot decode locally (large ScaledDecimals,
  LargeIntegers, DateAndTime/Date/Time) are converted in one round trip per load. Loading 3,000
  accounts with a `decimal` balance takes 75 GCI calls instead of 12,068.
- **Writing is batched too:** all changes go to the server in one batch, about six GCI calls,
  however many objects changed. Saving 2,700 changes and 300 new objects took 10 GCI calls
  instead of 7,509. The context only takes on the new state if the batch succeeds.
- **Loading again** returns the same C# instances, refreshed. If that would overwrite changes
  not saved yet, `Load` throws a `GsUnsavedChangesException` instead (or overwrites them with
  `GsLoadOptions { DiscardLocalChanges = true }`).
- **What it doesn't do:** it doesn't commit. If a save fails, the server may already hold part
  of the changes: abort, `Clear()` the context and reload.
- **Saved, then aborted:** an abort (also after a failed commit) takes back on the server what
  was saved but not committed, and the context follows: those objects count as changed again,
  and the next `SaveChanges` writes them again. `SaveChangesAndCommit()` relies on that to retry
  a conflict. Only aborts and commits through `GciSession` are noticed, not
  `System abortTransaction` in Smalltalk code.

Supported collection types:
- Array, OrderedCollection, SortedCollection → `List<T>` / `T[]`
- Set / IdentitySet → `HashSet<T>`
- Bag → `List<T>`
- any AbstractDictionary → `Dictionary<K,V>`

Changed collections are updated in place, not rewritten: only the elements that were removed,
added or replaced change (`removeIndex:` / `add:beforeIndex:` for ordered collections, `remove:` /
`add:` for sets and bags, `removeKey:` / `at:put:` for dictionaries). Elements the program didn't
touch stay the same objects. Elements that were added on the server after loading, for example
by Smalltalk code, are kept.

Collections can be nested (`List<List<T>>`, `Dictionary<string, List<T>>`, `T[][]` …). An inner
collection keeps its GemStone object as long as the program keeps the same C# instance, even
when it moves within the outer collection; its contents are updated in place as above. A new C#
instance becomes a new GemStone collection (OrderedCollection, Array, Set or KeyValueDictionary,
by its C# type).

#### Without reflection: the source generator

The package brings a source generator. In every project with `[GsClass]` types, it writes the
class `GemStone.Gci.Generated.GsGeneratedMappings` with all mapping metadata as plain code. Pass
it to the context, and the mapper needs no reflection, so trimmed and native AOT applications
work:

```csharp
using GemStone.Gci.Generated;

using var ctx = new GsObjectContext(session, GsGeneratedMappings.Default);
```

`new GsObjectContext(session)` keeps using reflection; the compiler warns about it in trimmed
and AOT projects (IL2026/IL3050). The generator covers:
- all `[GsClass]` types of the project and their C# subclasses,
- the types of their mapped properties (collections, nested collections),
- the `T` of `Load<T>` calls and of `Validate(typeof(T))`.

A type the generator never saw, for example only used as `Load<T>` inside generic code, makes
`Load` fail with a message saying so. Private setters, `init` setters and non-public
constructors work: on .NET 8 and later through `UnsafeAccessor`, on .NET Framework through
reflection. Mapping errors it can see at compile time (no setter, no parameterless
constructor, `[GsOop]` on a property that isn't an `Oop`, an unsuitable converter) appear as
warnings `GSGEN002`; types it cannot handle (private nested, generic) as `GSGEN001`. Tested
with a native AOT build (.NET 10) that loads objects and saves changes.

## Building and testing

```bash
dotnet build
dotnet test                       # net10.0 and net8.0; integration tests are skipped without a stone
tests/Net48Runner/run.sh          # the same tests against the net48 build, with Mono (Linux)
```

`dotnet test` runs the target frameworks one after another (on Windows also net48), because
they share the stone.

To run the integration tests, point them at a stone. They create only temporary objects; the
few keys they put into `UserGlobals` (`GemStoneGciTest_*`) are removed again, and the mapping
tests never commit.

```bash
export GEMSTONE=/path/to/GemStone64Bit3.7.5-x86_64.Linux
export GS_TEST_STONE=gs64stone
export GS_TEST_GEM_SERVICE='!@localhost#netldi:gs64ldi!gemnetobject'
export GS_TEST_USER=DataCurator GS_TEST_PASSWORD=swordfish
# optional: GS_TEST_HOST_USER, GS_TEST_HOST_PASSWORD
dotnet test
```

### Sample: `gsrepl`

`samples/GsRepl` is a small interactive Smalltalk console:
- Ctrl+C sends a soft break;
- a trailing `\` continues the input on the next line;
- `:commit`, `:abort` and `:q` commit, abort and quit.

```bash
GS_STONE=gs64stone GS_GEM_SERVICE='!@localhost#netldi:gs64ldi!gemnetobject' GS_PASSWORD=swordfish \
  dotnet run --project samples/GsRepl
dotnet run --project samples/GsRepl -- -e "3 + 4"

dotnet publish samples/GsRepl -c Release -r linux-x64   # self-contained single file
dotnet publish samples/GsRepl -c Release -r win-x64
```

## NuGet package

There is no published package yet. To build one locally:

```bash
dotnet pack -c Release -o artifacts       # artifacts/Gs64Net.Gci.0.1.0-alpha.nupkg
```

The package is called **`Gs64Net.Gci`**; the namespace in the code stays `GemStone.Gci`. (Package
IDs starting with `Gemstone.` are reserved on nuget.org for the unrelated Gemstone libraries of
the Grid Protection Alliance. Their namespace `Gemstone` differs in case from `GemStone`, so both
can be used in the same C# project.)

The package contains the net10.0, net8.0 and net48 builds with XML docs and embedded,
Source Link enabled symbols, but **not** the GemStone client library. To use it, add the
`artifacts` folder (or a network share) as a package source, in Visual Studio under
*Tools → Options → NuGet Package Manager → Package Sources*, or with
`dotnet nuget add source /path/to/artifacts --name gemstone-local`. The version comes from
`VersionPrefix`/`VersionSuffix` in `Directory.Build.props`; override it with `-p:Version=0.2.0`.

## Known GemStone issues

These were found while building the wrapper. The wrapper works around all of them.

| Issue | 3.6.5 | 3.6.8 | 3.7.2 | 3.7.5 | 3.7.6 | Workaround in the wrapper |
|---|---|---|---|---|---|---|
| `GciFetchPaths` crashes the gem (SIGSEGV in `checkFetchPathOffset`) when a path step starts from `nil` or another special object, as in `address.street` with `address == nil`. The session is lost. | yes | yes | yes | yes | yes | `FetchPaths` resolves one step per round trip and never steps from a special object. See [docs/bugreport-gcifetchpaths.md](docs/bugreport-gcifetchpaths.md) and the C program [repro/gcifetchpaths.cc](repro/gcifetchpaths.cc). |
| `GciExecuteStrTrav` leaves the source String in the PureExportSet on every call. | yes | yes | yes | yes | fixed | `ExecuteTraverse` uses `Execute` + `Traverse`. |
| `GciResolveSymbol` leaves a String with the name in the PureExportSet on every call. | yes | yes | yes | yes | fixed | `ResolveSymbol` uses `GciResolveSymbolObj` with its own String, which it releases. |
| `DateAndTime printString` fails ("unexpected format") when the NetLDI runs with a German `LC_NUMERIC`. | – | – | – | yes | – | Not a wrapper issue: start the NetLDI with `LC_NUMERIC=C`. The wrapper never parses locale-dependent `printString` output. |
| The compiler fails on `Utf8` source objects that contain a block ("Internal logic error in compiler: ComStrmSetCursor"). | yes | yes | yes | fixed | fixed | Non-ASCII source is sent as a Unicode string instead of `Utf8`. |
| `GciOopToFlt_` does not exist before 3.7. | missing | missing | – | – | – | `ToDouble` falls back to `GciOopToFlt`. |
| `GciGetFreeOops` hands out at most 100,000 OOPs per call and silently leaves the rest of the result array 0. | yes | yes | yes | yes | yes | `GetFreeOops` asks in chunks and checks for 0. |
| Windows client only: `GciGetFreeOops(n)` with n > 2,000 and not a multiple of 2,000 fails with error 2166 ("lgcUncompress failed decompression would exceed size of destination buffer"). The Linux client library is not affected. See [docs/bugreport-gcigetfreeoops.md](docs/bugreport-gcigetfreeoops.md) and the C program [repro/gcigetfreeoops.cc](repro/gcigetfreeoops.cc). | not tested | not tested | not tested | not tested | yes | `GetFreeOops` asks for whole blocks of 2,000 and keeps the rest for the next call. |

Other differences from the manual, taken from the 3.7.5 headers:
- `OopType` is 64 bits, not 32.
- `GciClampedTravArgsSType` has a different field order and an extra `resultOop` field.
- `GciResolveSymbol` returns `OOP_ILLEGAL` without an error for unknown names.
- The posix DateAndTime functions use the Unix epoch, not 1900.

## Project layout

```
src/GemStone.Gci/           the library
  Native/                   raw P/Invoke layer (internal), struct mirrors, library resolver
  Mapping/                  attribute-based object mapper (load + SaveChanges)
  GemStone.Gci.Generators/  source generator for the mapper (shipped in the package)
tests/GemStone.Gci.Tests/   unit tests and integration tests ([StoneFact])
tests/GemStone.Gci.Generators.Tests/  unit tests of the source generator
samples/GsRepl/             interactive console (separate solution)
docs/                       programming model, platform testing, bug reports
```

## Not implemented yet

- Clamped traversals
- Mapping Fraction values
- `GciContinue` after errors
- User actions
- Linked sessions (`libgcilnk`)
- The thread-safe GCI (`GciTs*`): planned as a separate package next to this one, which stays on the classic GCI
- A published NuGet package (it can be built locally, see above)

## License

[MIT](LICENSE). GemStone/S and GemBuilder for C are products of GemTalk Systems. This project is
not affiliated with GemTalk Systems.
