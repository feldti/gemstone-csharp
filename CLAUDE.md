# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Projekt

- **Zweck:** Aufbau einer C#-Schnittstelle, um auf GemStone/S-Datenbanken zugreifen zu können (Wrapper über GemBuilder for C / GCI).
- **GemStone-Version:** 3.7.x (GemStone/S 64 Bit)

## Befehle

```bash
dotnet build                                   # Bibliothek (net10.0 + net48) + Tests (GemStone.Gci.slnx)
dotnet test                                    # alle Tests (unter Linux nur net10.0)
tests/Net48Runner/run.sh                       # dieselben Tests gegen den net48-Build mit Mono (braucht mono-complete)
dotnet test --filter "FullyQualifiedName~GciErrSTypeLayoutTests"   # einzelne Testklasse

# Samples sind eigenständig (eigene .slnx, nicht Teil von GemStone.Gci.slnx), daher separat bauen:
dotnet build samples/GsRepl
dotnet publish samples/GsRepl -c Release -r linux-x64      # Single-File, self-contained, getrimmt
dotnet publish samples/GsRepl -c Release -r win-x64        # gsrepl.exe, auch von Linux aus
dotnet publish samples/GsRepl -c Release -r linux-x64 -p:GemStoneLibDir=$GEMSTONE/lib   # GemStone-Libs mitliefern
```

Gemeinsame Einstellungen (TargetFramework, Nullable, `TreatWarningsAsErrors`, eingebettete PDBs) stehen in `Directory.Build.props`; `global.json` verlangt SDK 10.0.x. Neue Samples als eigenes Verzeichnis unter `samples/` mit eigener `.slnx` anlegen, die das Bibliotheksprojekt per `ProjectReference` einbindet.

Integrationstests (`[StoneFact]`) werden übersprungen, solange `GS_TEST_STONE` nicht gesetzt ist. Weitere Variablen: `GS_TEST_USER`, `GS_TEST_PASSWORD`, `GS_TEST_HOST_USER`, `GS_TEST_HOST_PASSWORD`, `GS_TEST_GEM_SERVICE`, dazu `GEMSTONE` bzw. `GEMSTONE_GCI_LIBRARY`, damit die Bibliothek gefunden wird.

Lokaler Test-Stone auf diesem Rechner: `gs64stone` (3.7.5), NetLDI `gs64ldi` (`-g -a mf`), Login DataCurator/swordfish:

```bash
export GEMSTONE=/home/mf/Documents/GemStone/GemStone64Bit3.7.5-x86_64.Linux
export GS_TEST_STONE=gs64stone GS_TEST_GEM_SERVICE='!@localhost#netldi:gs64ldi!gemnetobject'
export GS_TEST_USER=DataCurator GS_TEST_PASSWORD=swordfish
dotnet test

# zweiter Stone mit 3.7.6: odatadev (Repository /datadisk/pas/work), NetLDI-Port 46019
# GEMSTONE=/datadisk/pas/work/products/GemStone64Bit3.7.6-x86_64.Linux GS_TEST_STONE=odatadev GS_TEST_GEM_SERVICE='!@localhost#netldi:46019!gemnetobject'
# Die GCI-Header (gci.hf, gci.ht, gcicmn.ht, gcioop.ht, gcioc.ht) sind in 3.7.5 und 3.7.6 identisch.

# dritter Stone mit 3.6.5: test365 (GsDevKit_stones-Registry work, /datadisk/pas/registry/gsdevkit_stones/stones/work/test365.ston), NetLDI test365_ldi, Port 37497
# GEMSTONE=/datadisk/pas/work/products/GemStone64Bit3.6.5-x86_64.Linux GS_TEST_STONE=test365 GS_TEST_GEM_SERVICE='!@localhost#netldi:37497!gemnetobject'

# vierter Stone mit 3.7.2: es2025 (Registry election, NetLDI es2025_ldi, Port 37441). Wirkt wie eine echte Datenbank:
# keine Absturz-Proben (GciFetchPaths) dort; die Suite selbst committet nur temporäre GemStoneGciTest_*-Schlüssel und räumt sie weg.
# GEMSTONE=/datadisk/pas/election/products/GemStone64Bit3.7.2-x86_64.Linux GS_TEST_STONE=es2025 GS_TEST_GEM_SERVICE='!@localhost#netldi:37441!gemnetobject'

# interaktive Konsole (eigene Variablen GS_STONE, GS_GEM_SERVICE, GS_USER, GS_PASSWORD bzw. --Optionen)
GS_GEM_SERVICE="$GS_TEST_GEM_SERVICE" GS_PASSWORD=swordfish dotnet run --project samples/GsRepl
dotnet run --project samples/GsRepl -- -e "3 + 4"     # einmalig auswerten
```

## Architektur

- `src/GemStone.Gci/Native/` — rohe Interop-Schicht, `internal`:
  - `GciNative.cs` — Einstiegspunkte als C#-Funktionszeiger (`delegate* unmanaged[Cdecl]<…>`), keine `[LibraryImport]`/`[DllImport]`. Der Grund: Unter .NET Framework gibt es keinen DllImport-Resolver, und die Datei heißt versioniert (`libgcirpc-3.7.5-64.dll`). Jede Funktion ist eine Wrapper-Methode mit verwalteter Signatur plus ein Feld `private static nint _Name`. Die Adresse holt `Fn(ref _Name, "Name")` beim ersten Aufruf. `BoolType` ist nativ `int` (`!= 0`), `const char[]` wird per `Utf8Z` + `fixed` als null-terminiertes UTF-8 übergeben. Neue Funktionen genau nach diesem Muster anlegen. Signaturen immer gegen `$GEMSTONE/include/gci.hf` prüfen, nicht nur gegen das Handbuch: `int` und `int64` bei Index- und Anzahl-Parametern unterscheiden sich je nach Funktion. Manche Namen sind Makros (`GciStoreBytes` → `GciStoreBytesInstanceOf`). `GciNativeExportTests` prüft über `GciNative.EntryPointNames()` (die `_Name`-Felder), dass jeder Einstiegspunkt in der Bibliothek existiert.
  - `GciLibraryResolver.cs` liefert die Kandidaten, `GciLibrary.cs` lädt einmalig per Pfad: unter .NET mit `NativeLibrary`, im net48-Build unter Windows mit `LoadLibraryExW(LOAD_WITH_ALTERED_SEARCH_PATH)`/`GetProcAddress`, unter Mono mit `dlopen`/`dlsym`. Die versionierte Datei (`.so`/`.dylib`/`.dll`, die höchste Version gewinnt) wird in dieser Reihenfolge gesucht: `GEMSTONE_GCI_LIBRARY` → `$GEMSTONE/lib` → `$GEMSTONE/bin` (Windows-Client) → Verzeichnis der Anwendung → OS-Suche.
  - `GciErrSType.cs` — blittable Spiegel der nativen Struktur mit Fixed Buffers; Offsets und Größe (2168 Byte) werden von `GciErrSTypeLayoutTests` abgesichert. Neue native Structs genauso anlegen und ebenso mit Layout-Tests absichern.
- `src/GemStone.Gci/` — öffentliche API: `GciSession` (`GciInit` einmal pro Prozess, `GciSetNet` + `GciLoginEx` mit `GciLoginFlags.Quiet` als Standard, `Dispose` → `GciLogout`) und `GciException` (aus `GciErr`).
- Die klassische GCI ist nicht thread-sicher und hat eine „aktuelle Session“ pro Prozess. Mehrere `GciSession`s sind trotzdem möglich: Jeder native Aufruf läuft unter dem prozessweiten Lock `GciSession.Sync` und hinter `Activate()`. `Activate()` wirft bei ausgeloggter Session und macht die eigene Session per `GciSetSessionId` zur aktuellen. Neue native Aufrufe immer hinter `Activate()` stellen. `SwitchTo()` (ohne Prüfung auf laufende Async-Operation) nur fürs Abfragen von Nb-Aufrufen und für `Dispose` verwenden. OOPs temporärer Objekte gehören zu ihrer Session.
- Async (`GciSession.Async.cs`): `GciNb*` starten, dann unter dem Lock `GciNbEndPoll(…, 0)` abfragen und außerhalb des Locks mit `Task.Delay` (1 → 32 ms) warten. In der Zwischenzeit arbeiten andere Sessions. Laufen N Sessions asynchron, laufen ihre Gems parallel (`SessionsExecuteInParallel`). Synchrone Aufrufe auf einer Session mit laufender Async-Operation → `InvalidOperationException`. Abbruch → `GciSoftBreak` → `OperationCanceledException`. Es gibt den Status `GCI_RESULT_HAS_ERROR = 3`, er steht nur in `gci.hf`.
- OOPs sind in der öffentlichen API der Typ `Oop` (`record struct` über einem `ulong`, gleiche Größe – `Perform` castet `Oop[]` direkt auf `OopType*`). Die Werte von `Oop.Nil`/`True`/`False`/`Illegal` stehen nicht im Handbuch, sondern stammen aus `gcioop.ht`.
- Ausführende Funktionen (`GciExecuteStr`, `GciPerform`, …) liefern bei Fehlern `OOP_NIL`, das aber auch ein gültiges Ergebnis ist. Deshalb bei `OOP_NIL` immer `GciErr` abfragen (`GciSession.CheckResult`). `GciErr` liefert jeden Fehler nur einmal. Nach einem fatalen Fehler ist die Session weg; `GciLogout` darf dann nicht mehr aufgerufen werden.
- `GciExecuteStr` kompiliert den C-String als 8-Bit-String, UTF-8 würde dabei verstümmelt. `Execute` schickt Nicht-ASCII-Quelltext deshalb als Unicode-String (`GciNewUtf8String_(…, utf8OrUnicode: true)` + `GciExecute`), **nicht als `Utf8`**. Der Compiler von 3.6.5 scheitert an `Utf8`-Quelltext, sobald ein Block darin vorkommt (`| d | d := [:x | x]. 'ö' size` → „Internal logic error in compiler: ComStrmSetCursor“). Unicode-Quelltext geht auf 3.6 und 3.7. Text wird per `printString` → `encodeAsUTF8` → `GciFetchBytes_` geholt (`PrintString`).
- PureExportSet (Handbuch 1.4): Ergebnisse von `GciNew*`, `GciCreate*`, `GciPerform*`, `GciExecute*` und `GciResolve*` sowie alle OOPs eines `GciErr`-Berichts landen automatisch dort. Inhalte von Instanzvariablen (`GciFetch*Oops`) dagegen nicht. Zwischenobjekte immer mit `Release` freigeben (überspringt Specials). `ThrowIfError` gibt die Fehler-OOPs selbst frei. `ErrorsAndEvaluateDoNotGrowTheExportSet` misst das über `System hiddenSetSize:`.
- Nach nicht-fatalen Ausführungsfehlern ruft der Wrapper `GciClearStack(context)` auf, ein `GciContinue` ist danach nicht mehr möglich. `GciSession.SoftBreak()` nimmt bewusst keinen Lock, weil es aus einem anderen Thread kommt.
- `samples/GsRepl` ist eine kleine REPL für manuelle Tests (Strg+C = Soft Break, `\` am Zeilenende = Fortsetzung, `:commit`/`:abort`/`:q`).
- Gegentest-Muster: Wenn eine neue Absicherung (Release, Session-Umschaltung …) gleich grün ist, kurz deaktivieren und prüfen, ob der Test dann wirklich fehlschlägt.
- Bekanntes GemStone-Problem, kein Wrapper-Bug: Läuft die NetLDI mit deutschem `LC_NUMERIC`, scheitert `DateAndTime printString` mit „unexpected format“ (`ScaledDecimal>>_printAsDateAndTimeANSISecondsOn:` erwartet `.`). Abhilfe: NetLDI mit `LC_NUMERIC=C` starten.
- Ohne `GciLoginFlags.Quiet` (`GCI_LOGIN_QUIET`) schreibt `libgcirpc` beim Login eine Zeile `gci login: …` nach stdout.
- Laufen ohne `GEMSTONE`: Auf Linux lädt `libgcirpc` zur Laufzeit per `dlopen` noch `libssl-*.so` und `libkrb5-*.so` (verifiziert mit 3.7.5). Alle drei müssen neben dem Binary liegen.
- `GciSession` ist auf mehrere Dateien verteilt (`partial`): Kern/Execute/Perform in `GciSession.cs`, Wertumwandlung in `GciSession.Values.cs`, Transaktionen in `GciSession.Transactions.cs`, struktureller Objektzugriff (Instanzvariablen, indizierte Slots, Bytes, NSCs, `NewObject`, `ResolveSymbol`) in `GciSession.Objects.cs`. Indizes sind wie in Smalltalk 1-basiert.
- `GciResolveSymbol` liefert bei unbekanntem Namen `OOP_ILLEGAL` ohne `GciErr` (steht nur im Header). **GemStone-Leck 3.7.5:** `GciResolveSymbol` lässt bei jedem Aufruf einen String mit dem Namen im PureExportSet, auch bei unbekannten Namen (auch in 3.6.5, in 3.7.6 behoben). `TryResolveSymbol` nutzt deshalb `GciResolveSymbolObj` mit einem eigenen String, den es wieder freigibt (`ResolveSymbolDoesNotGrowTheExportSet`).
- **Wertumwandlung:** `Oop` bildet die `GCI_*`-Makros (Tag-Bits aus `gcioop.ht`) in reinem C# nach: SmallInteger, Boolean, Character und die Typ-Prüfungen brauchen keinen Roundtrip. Alles andere läuft über GCI-Funktionen. `ToValue`/`FromValue` übernehmen die Zuordnung nil/bool/long/BigInteger/double/decimal/Rune/string/byte[]/DateTimeOffset/DateOnly/TimeOnly. Was nicht zuordenbar ist, kommt als `Oop` zurück. Feste Klassen-OOPs stehen in `KnownClasses` (aus `gcioop.ht`); `DateAndTime`, `Date` und `Time` stehen dort nicht und werden per Name aufgelöst. Date ↔ `DateOnly` über `asDays`/`fromDays:` (Tag 0 = 1901-01-01, proleptisch gregorianisch wie .NET); Time ↔ `TimeOnly` über `asMicroseconds`/`fromMicroseconds:`.
- Nichts über `printString` parsen, wenn es locale-abhängig ist. ScaledDecimal/Float drucken mit der Gem-Locale (`0,333`). `ToDecimal` rechnet deshalb serverseitig `(x * 10^scale) rounded`; `NewScaledDecimal` erzeugt ein Literal (`12.34s2`), das der Compiler locale-unabhängig liest.
- `GciNewDateAndTimePosix` und `GciFetchDateAndTimePosix` nutzen beide die Unix-Epoche (1970). Der Header-Kommentar „1900“ ist falsch (verifiziert mit `asPosixSeconds`). Die Auflösung ist Mikrosekunden.
- `GciCommit` liefert FALSE ohne `GciErr` → Konflikt (`TryCommit` = false, `Commit` wirft `GciTransactionConflictException`); FALSE mit `GciErr` → echter Fehler. Nach einem Konflikt muss vor dem Wiederholen ein `Abort` kommen. `RunInTransaction` macht genau das.
- Transaktionstests schreiben unter einem eindeutigen Schlüssel `GemStoneGciTest_*` in `UserGlobals` und räumen in `Dispose` auf. Konflikte werden mit einer zweiten `GciSession` im selben Prozess erzeugt. Übrig gebliebene Schlüssel findet man mit `(UserGlobals keys select: [:k | k beginsWith: 'GemStoneGciTest_'])`.
- Traversal (`GciSession.Traversal.cs`, `GciTraversal.cs`): Der Puffer liegt in eigenem nativem Speicher (8 Byte Header `allocatedBytes`/`usedBytes`, danach Object-Reports mit je 40 Byte Header plus Wertepuffer, auf 8 Byte ausgerichtet). Die Reports werden sofort in verwaltete `GciObjectReport`s kopiert. Ein Objekt, das größer als der Puffer ist, kommt als mehrere Teil-Reports mit steigendem `FirstOffset`; `GciTraversal` führt sie zusammen. `GciTraversal.ToValue` dekodiert Kernklassen (Strings in allen Breiten, Utf8, ByteArray, Float, Specials) ohne Roundtrip. `Oop.ToSmallDouble` ist aus `GCI_OOP_TO_SMALL_DOUBLE` portiert (8-Bit-Exponent, `1.0e100` ist keine SmallDouble).
- `GciClampedTravArgsSType` laut Header: `clampSpec, resultOop, travBuff, level, retrievalFlags, isRpc`. Das Handbuch nennt eine andere Reihenfolge und lässt `resultOop` weg. `isRpc` muss 1 sein (C++-Konstruktor).
- **GemStone 3.6.5:** `GciOopToFlt_` gibt es erst ab 3.7. `ToDouble` fällt auf `GciOopToFlt` zurück (Fehler über `GciErr`), `GciNativeExportTests` führt es als optional. Sonst ist die Bibliothek für den Wrapper gleich, und alle Tests laufen.
- **GemStone-Bug 3.6.5–3.7.6: `GciFetchPaths` bringt die Gem zum Absturz** (SIGSEGV in `checkFetchPathOffset` → `om::FetchSize_c(obj=0x14)`), wenn ein Pfadschritt *von* einem Spezialobjekt (nil, SmallInteger …) ausgeht; die Session ist dann weg. `FetchPaths` geht deshalb Schritt für Schritt vor (ein Roundtrip pro Pfadtiefe) und prüft vor jedem Schritt lokal `IsSpecial`. `GciFetchPaths` nie direkt mit mehrstufigen Pfaden aufrufen. Ungültige Indizes oder Byte-Objekte sind dagegen sicher (liefern `OOP_ILLEGAL`). Auch in 3.6.5 und 3.7.6 vorhanden (gleicher Stacktrace).
- **GemStone-Leck 3.7.5: `GciExecuteStrTrav`** lässt den Quelltext-String im PureExportSet, und seinen OOP bekommt man nicht. Auch in 3.6.5, in 3.7.6 behoben (Probe: 50 Aufrufe → +50 in 3.7.5, +0 in 3.7.6). `ExecuteTraverse` nutzt trotzdem `Execute` + `Traverse`, damit es mit 3.7.5 dicht bleibt. `GciPerformTrav` exportiert nur das Ergebnis; das wird ohne `KeepResult` wieder freigegeben.
- **Objekt-Mapping** (`src/GemStone.Gci/Mapping/`, Laden in `GsObjectContext.cs`, Schreiben in `GsObjectContext.Save.cs`): C#-Klassen mit `[GsClass("Name")]`/`[GsInstVar("ivName")]` (die Namen dürfen abweichen, die Klassen kommen vom Code-Generator des Nutzers), dazu `[GsCollection]`, `[GsConverter]`, `[GsOop]`, `[GsIgnore]`, `[GsReadOnly]`. `GsObjectContext.Load<T>`:
  - Ablauf: eine Traversal ab der Wurzel. Was das Mapping darüber hinaus braucht, wird gesammelt in weiteren Runden nachgeladen; `Depth` ist also nur eine Performance-Einstellung.
  - Dann werden die Objekte gebaut, iterativ und ohne Rekursion über den Objektgraphen. Eine Identity-Map sorgt dafür, dass Zyklen und geteilte Referenzen dieselbe C#-Instanz ergeben.
  - Polymorphie: Für eine GemStone-Unterklasse wird die passende C#-Unterklasse mit eigenem `[GsClass]` gewählt (aus derselben Assembly), sonst die erwartete Klasse.
  - Validierung: Bei der ersten Nutzung eines Typs prüft der Kontext Klasse und Instanzvariablen gegen das Image und sammelt alle Fehler mit Vorschlägen in einer `GsMappingException`. Danach werden die Metadaten verworfen.
  - Lesen der Collections: `Array`, `OrderedCollection` und `SortedCollection` haben keine `firstIndex`/`lastIndex`-Instanzvariablen, `IdentitySet`/`IdentityBag` sind NSCs. Beide werden direkt aus den Reports gelesen. `Set`/`Bag` halten ihre Elemente in einem internen `dict`, Dictionaries sind gehasht: Diese werden serverseitig flachgeklopft (`asArray` bzw. Schlüssel/Wert-Paare), ein Roundtrip pro Runde für alle zusammen.
  - Werte, die offline nicht lesbar sind (LargeInteger, ScaledDecimal, auch die Small*-Specials SmallScaledDecimal/SmallDate/SmallTime/SmallDateAndTime, DateAndTime-Objekte …), sammelt `Discover`. `Build` wandelt sie vorab in **einem** Batch um (`GciSession.ToValues`).
    - So funktioniert der Batch: Die Objekte kommen in ein Array, ein Block taggt sie serverseitig (`{1. intString}`, `{2. unscaledString. scale}`, `{3. posixMicrosString. offset}` …), und eine Traversal holt das Ergebnis.
    - Messwert: 3000 Accounts mit `decimal` brauchen 75 statt 12.068 GCI-Aufrufe. `ServerConvertedValuesAreBatched` sichert das über `GciSession.CallCount` ab (zählt jeden `Activate()`-Aufruf).
    - Stolperstein: `Utf8` ist in GemStone eine Unterklasse von `ByteArray`, keine `CharacterCollection`. Dekodiert wird sie mit `decodeToUnicode`.
  - Geerbte Properties müssen vom deklarierenden Typ geholt werden, sonst fehlt bei `private set` der Setter.
  - `SaveChanges`: Beim Laden (`TrackChanges`, Standard an) legt der Kontext pro Objekt und Property einen Snapshot an (OOP, Wert, bei Collections die Elemente). Die geladenen OOPs hält er per `GciSaveObjs` (`GciSession.Retain`) im Export-Set, bis `Dispose`/`Clear` sie freigibt. Ohne das könnte die Gem temporäre Objekte zwischen Laden und Speichern einsammeln. Eine Wurzel, die der Aufrufer per `Load(Oop)` übergibt, hält dagegen der Aufrufer.
    - Geschrieben wird nur, was vom Snapshot abweicht: Entities werden nach Referenz verglichen, alles andere nach Wert.
    - Neue Objekte entstehen per `new` auf der GemStone-Klasse.
    - Eine geänderte Collection wird als Ganzes in place neu befüllt (ein Block, dazu die Elemente als Array).
    - Strings und Enums, die Symbols waren, bleiben Symbols. Enums werden als Symbol mit kleinem Anfangsbuchstaben geschrieben (`#frozen`).
    - Ein Commit gehört nicht dazu.
  - `Stores` zählt auch die Instanzvariablen neu angelegter Objekte mit.
  - `Cast<DictionaryEntry>()` auf einem `IDictionary` liefert `KeyValuePair` (generischer Enumerator). Deshalb `IDictionary.GetEnumerator()` verwenden.
  - Die Session hält `Date`/`DateAndTime`/`Time` nach dem ersten Auflösen im Export-Set. Tests, die die Größe des Export-Sets messen, müssen alle beteiligten Pfade vorher einmal durchlaufen.
  - Reflection-basiert, daher `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]`; ein Source-Generator ist geplant. Die Tests (`GsMappingIntegrationTests`, `GsSaveChangesIntegrationTests`) legen Klassen und Daten in einer eigenen Session an und committen nie.
- Integrationstests laufen in der xUnit-Collection `"Stone"` ohne Parallelisierung, weil es nur eine GCI-Session pro Prozess gibt.
- `OopType` ist in GemStone/S 64 Bit **64 Bit** (`ulong`). Das Handbuch (Kap. 6.3) schreibt fälschlich „32-bit“.

## .NET Framework 4.8 (net48)

- **Ziele:** Die Bibliothek baut für `net10.0` und `net48` (`TargetFrameworks` im csproj; `TargetFramework` aus `Directory.Build.props` wird dort geleert). `net48` ist für bestehende WinForms-Anwendungen gedacht. Die müssen als **x64-Prozess** laufen, weil GemStone 3.7 nur 64-Bit-Client-DLLs liefert.
- **Pakete nur für net48:** `System.Memory`, `Portable.System.DateTimeOnly` (öffentliche `DateOnly`/`TimeOnly`), `PolySharp` (Compiler-Attribute) und die Referenz `System.Numerics`.
- **Ersatzfunktionen:** `src/GemStone.Gci/Polyfills/NetFrameworkPolyfills.cs` (nur `#if !NET`):
  - statische Erweiterungsmember aus C# 14 (`extension(ArgumentNullException) { ThrowIfNull … }`, `Environment.TickCount64`, `Encoding.Latin1`, `decimal.Scale` …), dazu klassische Erweiterungen (`TryDequeue`, `TryAdd`, `Chunk`);
  - `ReferenceEqualityComparer`, `NativeMemory` und `Ascii` als interne Typen;
  - ein **öffentliches** `System.Text.Rune`, damit die API auf beiden Zielen gleich bleibt.

  Fehlt etwas Neues aus der BCL, zuerst hier ergänzen. `#if` im Code nur, wo es nicht anders geht (`GeneratedRegex`, `IReadOnlySet`).
- **Nicht auf net48 verfügbar:**
  - Standard-Implementierungen in Interfaces, deshalb `IGsTwoWayConverter` als eigenes Interface;
  - Range-Zugriff auf Arrays (`arr[..n]` braucht `RuntimeHelpers.GetSubArray`), stattdessen `AsSpan(0, n).ToArray()`;
  - `nint` → `nuint`-Casts.
- **Tests gegen den net48-Build:** Das Testprojekt bekommt `net48` nur unter Windows oder mit `-p:WithNet48Tests=true`, weil `dotnet test` net48 unter Linux nicht hosten kann. Unter Linux startet `tests/Net48Runner/run.sh` einen minimalen xUnit-Runner (Reflection: Fact/Theory, InlineData/MemberData, Klassen-Fixtures, async) mit Mono. Mono braucht dafür seine Facade-Assemblies (`System.Runtime.dll` …, Paket `mono-complete`); die Referenz-Facades aus `Microsoft.NETFramework.ReferenceAssemblies` lädt Mono nicht. Mono ist nicht das echte .NET Framework; der endgültige Test ist ein Windows-Rechner.

## Verzeichnisstruktur

- `gemstone-c/` — Markdown-Konvertierung des Handbuchs *GemBuilder for C* (GemStone/S 64 Bit 3.7, Sept. 2023), aufbereitet für KI-Werkzeuge. **Nur lokal, nicht im Git-Repository** (Urheberrecht GemTalk, steht in `.gitignore`). In einem frischen Klon fehlt das Verzeichnis; dann direkt `$GEMSTONE/include/*.hf`/`*.ht` und das PDF-Handbuch nutzen.
  - `README.md` — Index aller Kapitel und aller Funktionen mit Einzeiler-Beschreibung. **Zuerst hier suchen**, statt Verzeichnisse zu durchlaufen.
  - `00-…06-*.md` — ein File pro Kapitel. Wichtig für den Wrapper: `03-building-gci-applications.md` (Linken gegen `libgcirpc`/`libgcilnk`), `05-compiling-and-linking.md`, `06-gembuilder-for-c-files-and-data-structures.md` (Header-Dateien, Typen und Structs wie `GciErrSType`, `GciObjInfoSType`, `GciTravBufType` — relevant für P/Invoke-Marshalling).
  - `07-gembuilder-c-function-reference/` — eine Datei pro GCI-Funktion bzw. Makro (`GciLogin.md`, `GCI_OOP_IS_SMALL_INT.md`, …); `00-overview.md` gibt den Überblick.
  - `08-gemstone-c-statistics-interface/` — GCSI-Funktionen (`Gcsi*`) für den Zugriff auf Shared-Cache-Statistiken.
  - Jede Datei beginnt mit einem Kommentar `<!-- Source: … PDF pages N–M -->`; Verweise "page N" sind als Links auf die jeweilige Datei umgesetzt.

## Hinweise für die C#-Anbindung

- Windows wird nur als Client unterstützt, und nur x64 (`GciSession.EnsureInitialized` prüft `Is64BitProcess`). Unter x64 gibt es nur eine Aufrufkonvention, die Header definieren nur `__declspec(dllimport)`; dieselben Funktionszeiger-Signaturen gelten also für Linux und Windows. `GCI_WINDOWS_CLIENT` setzt die Bibliothek selbst. Die Windows-Konsole muss für Nicht-ASCII-Zeichen auf UTF-8 umgestellt werden (siehe `samples/GsRepl/Program.cs`). Gegen einen echten Windows-Client ist das noch nicht getestet.

- Makros wie `GCI_OOP_IS_SMALL_INT`, `GCI_BOOL_TO_OOP` oder `GCI_CHR_TO_OOP` sind keine exportierten Funktionen; sie müssen in C# anhand der OOP-Bitkodierung nachgebaut werden (Details in der jeweiligen Makro-Datei).
- Die Dokumentation beschreibt die klassische (nicht thread-sichere) GCI (`gci.hf`); `gcilegacy.ht` enthält Definitionen, die nur dort und nicht in der thread-sicheren GCI genutzt werden.
