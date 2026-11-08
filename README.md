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
> What exists is covered by integration tests against GemStone 3.6.5, 3.7.2, 3.7.5 and 3.7.6 on Linux x86-64,
> for .NET 10 and .NET 8, and on a Windows x64 client (GemStone 3.7.6 client against a Linux
> stone) for .NET 10, .NET 8 and .NET Framework 4.8. Linux arm64 is supported by the code but
> has not been tested yet; see [docs/platform-testing.md](docs/platform-testing.md).

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
- 64-bit process only: Linux x86-64; Linux arm64 (GemStone 3.7 and later, e.g. a Raspberry Pi
  with a 64-bit OS; GemTalk supports it for development only); Windows x64 as a client

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
session.Commit();
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
- **What it doesn't do:** it doesn't commit. If a save fails, the server may already hold part
  of the changes: abort, `Clear()` the context and reload.

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

The mapper uses reflection, so it is not trimming- or AOT-safe yet. Nested collections
(`List<List<T>>`) cannot be saved yet.

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
dotnet pack -c Release -o artifacts       # artifacts/GemStone.Gci.0.1.0-alpha.nupkg
```

The package contains the net10.0, net8.0 and net48 builds with XML docs and embedded,
Source Link enabled symbols, but **not** the GemStone client library. To use it, add the
`artifacts` folder (or a network share) as a package source, in Visual Studio under
*Tools → Options → NuGet Package Manager → Package Sources*, or with
`dotnet nuget add source /path/to/artifacts --name gemstone-local`. The version comes from
`VersionPrefix`/`VersionSuffix` in `Directory.Build.props`; override it with `-p:Version=0.2.0`.

## Known GemStone issues

These were found while building the wrapper. The wrapper works around all of them.

| Issue | 3.6.5 | 3.7.2 | 3.7.5 | 3.7.6 | Workaround in the wrapper |
|---|---|---|---|---|---|
| `GciFetchPaths` crashes the gem (SIGSEGV in `checkFetchPathOffset`) when a path step starts from `nil` or another special object, as in `address.street` with `address == nil`. The session is lost. | yes | not tested | yes | yes | `FetchPaths` resolves one step per round trip and never steps from a special object. See [docs/bugreport-gcifetchpaths.md](docs/bugreport-gcifetchpaths.md). |
| `GciExecuteStrTrav` leaves the source String in the PureExportSet on every call. | yes | yes | yes | fixed | `ExecuteTraverse` uses `Execute` + `Traverse`. |
| `GciResolveSymbol` leaves a String with the name in the PureExportSet on every call. | yes | yes | yes | fixed | `ResolveSymbol` uses `GciResolveSymbolObj` with its own String, which it releases. |
| `DateAndTime printString` fails ("unexpected format") when the NetLDI runs with a German `LC_NUMERIC`. | – | – | yes | – | Not a wrapper issue: start the NetLDI with `LC_NUMERIC=C`. The wrapper never parses locale-dependent `printString` output. |

| The 3.6.5 compiler fails on `Utf8` source objects that contain a block ("Internal logic error in compiler: ComStrmSetCursor"). | yes | no | no | no | Non-ASCII source is sent as a Unicode string instead of `Utf8`. |
| `GciOopToFlt_` does not exist before 3.7. | missing | – | – | – | `ToDouble` falls back to `GciOopToFlt`. |
| `GciGetFreeOops` hands out at most 100,000 OOPs per call and silently leaves the rest of the result array 0. | yes | not tested | not tested | yes | `GetFreeOops` asks in chunks and checks for 0. |
| Windows client only: `GciGetFreeOops(n)` with n > 2,000 and not a multiple of 2,000 fails with error 2166 ("lgcUncompress failed decompression would exceed size of destination buffer"). The Linux client library is not affected. See [docs/bugreport-gcigetfreeoops.md](docs/bugreport-gcigetfreeoops.md). | not tested | not tested | not tested | yes | `GetFreeOops` asks for whole blocks of 2,000 and keeps the rest for the next call. |

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
tests/GemStone.Gci.Tests/   unit tests and integration tests ([StoneFact])
samples/GsRepl/             interactive console (separate solution)
docs/                       bug reports and notes
```

## Not implemented yet

- A source generator for the mapper, for AOT/trimming
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
