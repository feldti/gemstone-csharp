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
  GemStone object becomes exactly one C# object (per C# class).
- **`SaveChanges`** compares with the state at load time and writes only the differences, in
  one batch of about six GCI calls, however many objects changed. Collections are changed in
  place, nested ones too. New C# objects are created on the server with `new` on their
  GemStone class.
- **The commit is separate:** `SaveChanges` writes into the session's transaction. Only
  `Commit` (or `RunInTransaction`) makes it permanent. `ctx.SaveChangesAndCommit()` does both
  and retries a conflict.
- **An abort takes back what was saved but not committed**, and the context follows: after
  `session.Abort()` (also after a failed commit) those objects count as changed again, and the
  next `SaveChanges` writes them again. New objects keep their GemStone objects; only the
  references to them are written again. The context notices aborts and commits through
  `GciSession` (`Abort`, `Commit`, `Begin`, their `Async` versions, `RunInTransaction`), not
  `System abortTransaction` in Smalltalk code.

## Loading the same object twice

A context has an identity map: each GemStone object becomes exactly one C# object (one per
C# class, if you map the same GemStone class with several C# classes; see
[Large object graphs](#large-object-graphs)).

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

## Large object graphs

A GemStone database is one large graph: everything is reachable from a few roots, and most
associations have a way back (`market → company`, `region → parent`), plus cross references
(neighbours, the same market in earlier years, all markets of one type).
`Load` follows every mapped property, so a C# class that maps everything loads everything it
can reach, often the whole database.

The mapping decides where loading stops. Map per screen, not per GemStone class:

```csharp
[GsClass("Company")]
public class CompanyView
{
    [GsInstVar("name")] public string Name { get; set; } = "";
    [GsInstVar("markets"), GsCollection(GsCollectionKind.Ordered)] public List<MarketView> Markets { get; set; } = [];
}

[GsClass("Market")]
public class MarketView
{
    [GsInstVar("key")] public long Key { get; set; }
    [GsInstVar("company")] public Oop Company { get; set; }             // the way back: not loaded
    [GsInstVar("topRegion")] public RegionView? Top { get; set; }
}

[GsClass("Region")]
public class RegionView
{
    [GsOop] public Oop Oop { get; private set; }
    [GsInstVar("key")] public string Key { get; set; } = "";
    [GsInstVar("parent")] public Oop Parent { get; set; }
    [GsInstVar("children"), GsCollection(GsCollectionKind.Ordered)] public List<RegionView> Children { get; set; } = [];
    [GsInstVar("neighbours"), GsCollection(GsCollectionKind.IdentitySet)] public List<Oop> Neighbours { get; set; } = [];
}

var company = ctx.Load<CompanyView>("Companies current");
var district = company.Markets[0].Top!.Children[3].Children[2];    // plain C# from here on
```

- **Unmapped instance variables** are not loaded at all.
- **A property of type `Oop`** (or `List<Oop>`) keeps the reference without loading the
  target. Use it for ways back to the parent and for cross references. Load the target when it
  is needed: `ctx.Load<RegionView>(district.Parent)`. An `Oop` property nobody set is written
  as nil.
- **A level with many objects** can be a `List<Oop>` and be loaded when a window opens it. Load
  such a list in one call: `ctx.Load<List<RegionView>>("… children asArray")` took 1 ms
  for 16 objects, 16 single `Load` calls 60 ms.
- **A detail class** maps the same GemStone class with more properties (contacts, notes …). In
  the same context it is a second instance next to the overview instance: both are tracked,
  `SaveChanges` writes what changed in either, and `Refresh` updates both.
  `ctx.GetEntities(oop)` answers all instances of an object.
- **Computed attributes** are methods, not instance variables, and are not mapped. Call them:
  `session.Send(district.Oop, "displayString")`.
- **Associations with two ends are not kept in sync.** `SaveChanges` writes instance variables;
  it does not run the setters of the domain classes. A new region added to `Children` does not
  get its `parent`, and it does not appear in other collections such as the market's list of
  all regions. Set both ends in C#, or create such objects through a server method that does.

Measured with a cut-out of a real model of this shape on 3.7.6 (3 roots with 2 subtrees each,
3,377 regions per subtree, back references and cross references as above):

| | Objects in C# | Time | GCI calls |
|---|---|---|---|
| Everything mapped, load one root | 40,543 (all three roots) | 1.2 s | 1,037 |
| View classes as above | 6,757 (this root, its regions) | 108 ms | 142 |
| `Oop` for the sub-regions, load the 16 top-level regions one by one / as one list | 18 / 16 | 60 ms / 1 ms | 128 / 5 |
| `Refresh` of one top-level region with its 210 sub-regions | | 9 ms | 11 |

A whole graph of about 825,000 objects, with change tracking and without (for a read-only
replica that `Refresh` keeps up to date): see `samples/ReplicaBench`. Locally on 3.7.6 it loads
in 9.1 s / 5.1 s and takes 1,154 / 314 MB; almost all of the time is CPU in the .NET process.

## Using the domain logic on the server

Methods of the GemStone classes stay there and are called, not rewritten in C#:

```csharp
session.Send(c.Oop, "applyDiscount:", 10);         // a Smalltalk method of the loaded object
ctx.Load<Customer>(c.Oop);                          // load again to see the server's state
```

`Send` converts arguments and the result like `Evaluate`; `Perform` works on plain OOPs.

## Notifications from the server

A `GciSignalListener` receives two kinds of notifications, on a session of its own:

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
    session.Abort();                                 // see what the others committed
    ctx.Refresh(e.Signals.ChangedObjects);           // reload them into the same instances
    foreach (var m in e.Signals.Messages)
        Console.WriteLine($"session {m.SenderSerial} sent {m.Value}: {m.Message}");
};
listener.Start();                                    // waits on the server, reports within milliseconds

// elsewhere, in another session or program:
otherSession.SendSignal(listener.SessionId, 42, "price list updated");
```

In Smalltalk the same signal is `System sendSignal: 42 to: listenerSessionId withMessage: '…'`.

Things to know:
- **It is (almost) push:** `Start()` keeps one call waiting on the server, which returns as soon
  as something arrives. In our measurements the event came 0.5–0.7 ms (median) after the other
  session's commit or signal, without using CPU while waiting. The GCI stays free meanwhile;
  other sessions of the process keep working.
  `Start(interval)` polls instead, `Poll()` and `WaitAsync()` do one step yourself.
- **Use a session that does nothing else:** each wait or poll aborts it, and a wait occupies it.
  `Watch`, `Unwatch` and `Poll` interrupt a wait in progress (nothing gets lost).
- **A notification is a hint, not the data.** To see the change, the session that works with
  the objects aborts (or commits) and loads them again: `ctx.Refresh(oops)` reloads all
  reported objects in one load, into the same instances. A reported collection refreshes the
  object that holds it (also for objects loaded with `TrackChanges = false`, such as a read-only
  replica); OOPs the context has not loaded are ignored. Unsaved local changes are
  protected: `Refresh` throws a `GsUnsavedChangesException` instead of overwriting them.
- **Signal texts are byte Strings on the server:** only characters up to U+00FF (Latin-1).
  `SendSignal` rejects anything else with an `ArgumentException`.
- **Signals are not stored permanently.** They live in the receiving session until read, and
  disappear with it. For events that must not get lost (for example while no listener is
  running), let the senders commit entries to a persistent queue and use the signal only as a
  prompt to look.

How it works, the measurements, and the GemStone behaviour behind the design:
[notifications.md](notifications.md).

## A desktop application: several windows and an input form

Say windows A and B show customers and orders, and in form C the user is entering a new order
or editing a customer. What happens, step by step:

1. **While the user types in C,** the data exists only in C# objects. Nothing goes to the
   server: no locks, and other users see nothing of it.
2. **Windows A and B do not change by themselves,** even when other users commit changes to
   what they show. Loaded objects are copies. A `GciSignalListener` tells you which objects
   changed; you decide whether to reload a window.
3. **Reloading a window** means `session.Abort()` (to see what others committed) and `Load`
   again. The abort affects only the server; the C# objects of the other windows, and the
   input in C, stay as they are.
4. **If A and C show the same object** (A lists customers, C edits one of them) and share a
   context, it is the same C# instance: what the user types in C already changes the object A
   shows. Whether A displays that at once depends on data binding (`INotifyPropertyChanged`
   on your classes). If A reloads while C has unsaved changes on that object, `Load` throws a
   `GsUnsavedChangesException` rather than overwrite them; ask the user, and reload with
   `DiscardLocalChanges` only if they agree.
5. **Saving C** writes the changes and commits:

   ```csharp
   try
   {
       ctx.SaveChanges();
       session.Commit();
   }
   catch (GciTransactionConflictException)
   {
       // Someone else committed changes to the same objects in the meantime.
       session.Abort();                 // the saved changes are unsaved again, nothing is lost
       // Either ask the user, or simply write the changes again on top of the new state:
       ctx.SaveChanges();
       session.Commit();
   }
   ```

   `ctx.SaveChangesAndCommit()` does the same in one call (with up to three attempts).
   Properties the user changed overwrite what the other session committed to the *same*
   properties; everything else the other session changed is kept, including elements it added
   to collections.

Because an abort takes back saved changes that were not committed (and the context marks them
as changed again), another window that aborts to refresh between your `SaveChanges` and your
`Commit` does not lose your input: it is simply written again by the next save. Still, keep
save and commit together; it is what users expect from a "Save" button.

How to divide sessions and contexts:

| Layout | Good | Watch out |
|---|---|---|
| **One work session and one context** for the application, plus a listener session | Each GemStone object is one C# object in every window; simple | `Abort`/`Commit` apply to all windows at once; one window's "Save" commits the other windows' saved changes too |
| **One session and context per form** (plus the shared listener) | The form has its own transaction: saving or cancelling it does not touch other windows | The same customer in two windows is two C# objects; each session is a gem process on the server |

A middle way is one session for all the windows that only display data, and an own session for
each form that edits. "Cancel" in such a form is just disposing its context (and aborting its
session).

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
