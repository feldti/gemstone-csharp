# gemstone-csharp

**🚧 Work in progress: not ready for production use. 🚧**

A .NET 10 and .NET Framework 4.8 wrapper for **GemStone/S 64 Bit 3.7.x**, built on the classic (non-thread-safe)
**GemBuilder for C** (GCI, `libgcirpc`). It lets C# programs log in to a stone, run Smalltalk,
convert values, run transactions, read and write objects, and load object graphs into C# classes.

> [!WARNING]
> **Work in progress.** This project is under active development and not ready for production
> use. APIs may change without notice, there are no releases or NuGet packages yet, and some
> parts are missing (see [Not implemented yet](#not-implemented-yet)).
>
> What exists is covered by integration tests against GemStone 3.7.5 and 3.7.6 on Linux x86-64.
> The Windows client support is implemented but has not been tested against a real Windows
> client yet. The .NET Framework 4.8 build compiles, but has not been run on a real .NET
> Framework installation yet.

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
  a round trip.
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

- .NET SDK 10.0 to build. The library targets **.NET 10** and **.NET Framework 4.8**; the
  latter is for existing Windows applications (WinForms, WPF), which must run as **x64** processes.
- The GemStone/S 64 Bit 3.7.x client library (`libgcirpc-3.7.x-64.so`, or `.dll` on Windows),
  usually taken from a GemStone installation (`$GEMSTONE`)
- A running stone and NetLDI to connect to
- 64-bit process only: Linux x86-64; Windows x64 as a client

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
accounts[0].Orders.Add(new Order { Number = 42 });     // collection rewritten in place
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
- **What it doesn't do:** it doesn't commit. If a save fails halfway, abort, `Clear()` the
  context and reload.

Supported collection types:
- Array, OrderedCollection, SortedCollection → `List<T>` / `T[]`
- Set / IdentitySet → `HashSet<T>`
- Bag → `List<T>`
- any AbstractDictionary → `Dictionary<K,V>`

The mapper uses reflection, so it is not trimming- or AOT-safe yet. A changed collection is
rewritten as a whole, and nested collections (`List<List<T>>`) cannot be saved yet.

## Building and testing

```bash
dotnet build
dotnet test                       # unit tests; integration tests are skipped without a stone
```

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

## Known GemStone issues

These were found while building the wrapper. The wrapper works around all of them.

| Issue | 3.7.5 | 3.7.6 | Workaround in the wrapper |
|---|---|---|---|
| `GciFetchPaths` crashes the gem (SIGSEGV in `checkFetchPathOffset`) when a path step starts from `nil` or another special object, as in `address.street` with `address == nil`. The session is lost. | yes | yes | `FetchPaths` resolves one step per round trip and never steps from a special object. See [docs/bugreport-gcifetchpaths.md](docs/bugreport-gcifetchpaths.md). |
| `GciExecuteStrTrav` leaves the source String in the PureExportSet on every call. | yes | fixed | `ExecuteTraverse` uses `Execute` + `Traverse`. |
| `GciResolveSymbol` leaves a String with the name in the PureExportSet on every call. | yes | fixed | `ResolveSymbol` uses `GciResolveSymbolObj` with its own String, which it releases. |
| `DateAndTime printString` fails ("unexpected format") when the NetLDI runs with a German `LC_NUMERIC`. | yes | – | Not a wrapper issue: start the NetLDI with `LC_NUMERIC=C`. The wrapper never parses locale-dependent `printString` output. |

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

- Incremental collection updates and batched writes (store traversals) for `SaveChanges`
- A source generator for the mapper, for AOT/trimming
- Clamped traversals
- Mapping Fraction values
- `GciContinue` after errors
- User actions
- Linked sessions (`libgcilnk`)
- The thread-safe GCI (`GciTs*`); this wrapper deliberately uses the classic GCI
- A NuGet package

## License

[MIT](LICENSE). GemStone/S and GemBuilder for C are products of GemTalk Systems. This project is
not affiliated with GemTalk Systems.
