# Gs64Net.Gci

**Work in progress: not ready for production use. APIs may change.**

A .NET 10, .NET 8 and .NET Framework 4.8 wrapper for **GemStone/S 64 Bit 3.6 and 3.7**, built on the
classic (non-thread-safe) **GemBuilder for C** (GCI, `libgcirpc`).

- Sessions over NetLDI, several sessions per process
- Smalltalk execution (`Execute`, `Perform`, `Evaluate<T>`), async calls with soft break
- Value conversion between GemStone and .NET (integers, ScaledDecimal ↔ `decimal`, strings,
  DateAndTime ↔ `DateTimeOffset`, Date ↔ `DateOnly` …)
- Transactions with conflict detection and retry
- Traversals: whole object graphs in one round trip
- Object mapper: load GemStone objects into your own attributed C# classes, change them and
  write the changes back in one batch with `SaveChanges`. A source generator in this package
  writes the mapping metadata at compile time (`GemStone.Gci.Generated.GsGeneratedMappings`),
  for trimmed and native AOT applications:
  `new GsObjectContext(session, GsGeneratedMappings.Default)`

## Requirements

- The GemStone client library (`libgcirpc-3.x.y-64.so` / `.dll`) is **not** part of this
  package. It is found through `GEMSTONE_GCI_LIBRARY` (full path), `$GEMSTONE/lib` or
  `$GEMSTONE/bin`, the application directory, or the OS library search.
- 64-bit processes only: Linux x86-64 and Windows x64; with .NET 10 only, also Linux arm64
  (e.g. a Raspberry Pi with a 64-bit OS) and macOS on Apple Silicon (GemStone 3.7 and later). For .NET Framework applications, set the platform target
  to `x64`, or turn off "Prefer 32-bit".

## Quick start

```csharp
using GemStone.Gci;

var net = new GciNetParameters(
    StoneName: "gs64stone",
    GemService: "!@localhost#netldi:gs64ldi!gemnetobject");

using var session = GciSession.Login(net, "DataCurator", "swordfish");
long sum = session.Evaluate<long>("3 + 4");
```

Documentation, known GemStone issues and the source code:
https://github.com/feldti/gemstone-csharp
