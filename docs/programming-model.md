# Programming model: GemStone/S from C#

In C# you work with **copies** of GemStone objects, not with live proxies. The data and the
domain logic stay in the stone. The C# program is a client that runs Smalltalk, loads object
graphs into its own classes and writes changes back in one batch. Committing is always up to
you.

## Three layers

| Layer | What for | Main types |
|---|---|---|
| 1. Session | connection, running Smalltalk, transactions | `GciSession` |
| 2. Objects directly | single OOPs, instance variables, traversals | `Oop`, `Traverse`, `FetchPaths` … |
| 3. Object mapper | object graphs ↔ your own C# classes | `GsObjectContext`, `[GsClass]` |

Most applications need layers 1 and 3. Layer 2 is for special cases and tools.

## 1. The session: Smalltalk is the query language

```csharp
using GemStone.Gci;

using var session = GciSession.Login(
    new GciNetParameters(StoneName: "gs64stone", GemService: "!@localhost#netldi:gs64ldi!gemnetobject"),
    "DataCurator", "swordfish");

long count = session.Evaluate<long>("Customers size");
decimal total = session.Evaluate<decimal>("Customers inject: 0 into: [:sum :c | sum + c balance]");
```

Searching, filtering and aggregating happen in the gem, in Smalltalk (`select:`, `detect:`,
indexes). Only the results travel to C#. There is deliberately no LINQ provider that
translates to Smalltalk.

Results are converted to .NET values where there is a natural match: integers, `decimal`
(ScaledDecimal), `double`, strings and Symbols, `DateTimeOffset` (DateAndTime), `DateOnly`,
`TimeOnly`, `byte[]`. Anything else comes back as an `Oop`.

## 2. The object mapper: load, change, `SaveChanges`

You write (or generate) C# classes that match the GemStone classes. Names may differ; the
attributes connect them:

```csharp
using GemStone.Gci.Mapping;

[GsClass("Customer")]
public class Customer
{
    [GsOop] public Oop Oop { get; private set; }
    [GsInstVar("name")] public string? Name { get; set; }
    [GsInstVar("balance")] public decimal Balance { get; set; }
    [GsInstVar("orders"), GsCollection(GsCollectionKind.Ordered)] public List<Order> Orders { get; set; } = [];
}
```

The usual flow is a unit of work:

```csharp
using GemStone.Gci.Generated;

using var ctx = new GsObjectContext(session, GsGeneratedMappings.Default);

session.Abort();                                    // start from the latest committed state
session.RunInTransaction(s =>
{
    ctx.Clear();                                    // a retry after a conflict loads afresh
    var c = ctx.Load<Customer>("Customers detect: [:x | x id = 4711]");
    c.Balance += 100m;
    c.Orders.Add(new Order { Number = 42 });        // a new object
    ctx.SaveChanges();                              // one batch, only the changes
});                                                 // commits; on a conflict: abort and retry
```

What happens:
- **`Load`** fetches the graph in a few round trips: traversals, plus one batch for values
  such as `decimal` or dates. Cycles and shared references are kept: within a context, each
  GemStone object becomes exactly one C# object.
- **`SaveChanges`** compares with the state at load time and writes only the differences, in
  one batch of about six GCI calls, however many objects changed. Collections are changed in
  place, nested ones too. New C# objects are created on the server with `new` on their
  GemStone class.
- **The commit is separate:** `SaveChanges` writes into the session's transaction. Only
  `Commit` (or `RunInTransaction`) makes it permanent.

## Loading the same object twice

A context has an identity map: each GemStone object becomes exactly one C# object.

- **Twice in one `Load`** (two customers share an address, or a cycle such as
  `account → orders → account`): one instance, filled once; both places refer to it.
- **Again in a later `Load` of the same context:** you get the same instance back, refreshed
  with the server's state. Inside a transaction, that state is the transaction's snapshot plus
  your own session's changes; commits of other sessions appear only after `Commit` or `Abort`.
- **Unsaved changes are protected:** if the load would refresh an instance with changes that
  `SaveChanges` has not written yet, it throws a `GsUnsavedChangesException` (with the
  affected objects in `Entities`) before it changes anything. Save first, or load with
  `new GsLoadOptions { DiscardLocalChanges = true }` to throw the changes away on purpose.
  Objects the load does not reach are not affected. Properties with `[GsReadOnly]` and objects
  loaded with `TrackChanges = false` have no snapshot and are simply refreshed.
- **Another context** has its own identity map: the same GemStone object becomes a separate C#
  object there, with its own change tracking.

## What is different from an ORM

1. **Persistence by reachability:** there is no insert into a table. A new object is stored
   permanently only if a persistent object refers to it when the transaction commits. In the
   example above, that is the order in `c.Orders`. An object you only `Add` to the context
   stays temporary until something persistent points to it.
2. **Snapshot view:** each session sees the state of its own transaction. Changes of other
   sessions become visible only after `Commit` or `Abort` and loading again. Loaded C# objects
   do not update themselves.
3. **Conflicts instead of locks:** if another session has already changed and committed the
   same object, your commit fails. Then abort, load again and retry; `RunInTransaction` does
   that, and its work must be repeatable. It does not abort before the first attempt, so call
   `session.Abort()` first if you need the latest state.

## Using the domain logic on the server

Methods of the GemStone classes stay there and are called, not rewritten in C#:

```csharp
session.Send(c.Oop, "applyDiscount:", 10);         // a Smalltalk method of the loaded object
ctx.Load<Customer>(c.Oop);                          // load again to see the server's state
```

`Send` converts arguments and the result like `Evaluate`; `Perform` works on plain OOPs.

## Notifications from the server

The server cannot interrupt a client. A `GciSignalListener` asks instead, on a session of its
own, and receives two kinds of notifications:

- **Changed objects:** you watch committed objects; when another session commits a change to
  one of them, the listener reports it (GemStone's notify set).
- **Signals between sessions:** any session (C# or Smalltalk code on the server) can send a
  number and a text to the listener's session.

```csharp
using var listenerSession = GciSession.Login(net, "DataCurator", "swordfish"); // only for the listener
using var listener = new GciSignalListener(listenerSession);

ctx.TryGetOop(customer, out var oop);
listener.Watch(oop);                                 // committed objects only
listener.SignalsReceived += (_, e) =>                // on the UI thread if Start was called there
{
    foreach (var changed in e.Signals.ChangedObjects)
        if (ctx.TryGetEntity(changed, out var entity)) MarkStale(entity);
    foreach (var m in e.Signals.Messages)
        Console.WriteLine($"session {m.SenderSerial} sent {m.Value}: {m.Message}");
};
listener.Start(TimeSpan.FromSeconds(1));             // or call listener.Poll() yourself

// elsewhere, in another session or program:
otherSession.SendSignal(listener.SessionId, 42, "price list updated");
```

In Smalltalk the same signal is `System sendSignal: 42 to: listenerSessionId withMessage: '…'`.

Things to know:
- **Use a session that does nothing else.** With signals enabled, any Smalltalk the session
  runs can fail with GemStone error 6008 or 6010 when a signal arrives. The listener handles
  that (and so loses no signal); other code would not.
- **A notification is a hint, not the data.** To see the change, the session that works with
  the objects aborts (or commits) and loads them again. Unsaved local changes are protected:
  that load throws a `GsUnsavedChangesException` instead of overwriting them.
- **Each poll costs one round trip** and aborts the listener session, so it never holds an old
  view of the repository. A poll interval of a second or so is plenty for most applications.
- **Signal texts are byte Strings on the server:** only characters up to U+00FF (Latin-1).
  `SendSignal` rejects anything else with an `ArgumentException`.
- **Signals are not stored permanently.** They live in the receiving session until read, and
  disappear with it. For events that must not get lost (for example while no listener is
  running), let the senders commit entries to a persistent queue, such as an `RcQueue`, and
  use the signal only as a prompt to look.

## Threads and several sessions

- The classic GCI exists once per process, but **several sessions** are possible. Their calls
  run one after another, under a lock.
- Several gems work truly in parallel with **`EvaluateAsync`** on several sessions. In a UI,
  `await` keeps the window responsive; a `CancellationToken` sends a soft break.
- A `GsObjectContext` belongs to one session and is not thread-safe. Create one context per
  unit of work and dispose it afterwards: it holds the loaded objects in the session's export
  set, so the gem cannot garbage-collect them until then.

## Reflection or source generator

| Constructor | When |
|---|---|
| `new GsObjectContext(session)` | reflection: convenient, e.g. for .NET Framework / WinForms |
| `new GsObjectContext(session, GsGeneratedMappings.Default)` | no reflection: trimmed apps and native AOT, faster startup |

The classes and attributes are the same in both cases. Switching only means changing the
constructor. The source generator ships in the `Gs64Net.Gci` package and runs in every
project with `[GsClass]` types; see "Without reflection: the source generator" in the
[README](../README.md).

## Layer 2: objects without the mapper

When you do not want C# classes for something, work with OOPs directly. These calls do the
same job as the mapper, but one step at a time:

```csharp
Oop c = session.Execute("Customers detect: [:x | x id = 4711]");
try
{
    string? name = session.Send<string>(c, "name");
    var graph = session.Traverse(c, level: 2);       // the object and what it references, in one round trip
}
finally
{
    session.Release(c);                              // results of Execute stay in the export set until released
}
```

Results of `Execute`, `Perform` and the `New*` methods are kept in the session's export set
(so the gem cannot collect them) until you `Release` them. The mapper does this bookkeeping
for you.
