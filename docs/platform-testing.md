# Testing on Windows and on Linux arm64 (Raspberry Pi)

The test suite runs on Linux x86-64 against GemStone 3.6.5, 3.7.2, 3.7.5 and 3.7.6, for
.NET 10, .NET 8 and (with Mono) .NET Framework 4.8. Two platforms are supported by the code
but have not been tested yet: Windows x64 as a client, and Linux arm64.

## Windows x64 (client)

GemStone runs only as a client on Windows: the stone and the gems stay on the Linux server,
and the Windows program connects to them over the network.

### What you need

| What | Where from | Notes |
|---|---|---|
| Windows 10/11 x64 (a VM is fine) | | |
| GemStone Windows client, **same version as the stone** | GemTalk download page, e.g. `GemStone64BitClient3.7.5-x86.Windows_NT.zip` | Contains `bin\libgcirpc-3.7.5-64.dll` and the DLLs it needs. The client library must match the server version. |
| .NET 10 SDK | https://dotnet.microsoft.com/download | Builds everything; runs the net10.0 tests |
| .NET 8 runtime (x64) | same page | Runs the net8.0 tests |
| .NET Framework 4.8 or 4.8.1 | part of current Windows | `dotnet test` runs the net48 tests natively on Windows (no Mono) |
| Git | https://git-scm.com | To clone the repository |
| Network access to the Linux server | | NetLDI port, plus the ports of the gems the NetLDI starts (see below) |

### Server side

- The NetLDI must accept logins from another machine. With guest mode (`startnetldi -g -a <user>`,
  as for `gs64ldi`) no host password is needed; otherwise set `GS_TEST_HOST_USER` and
  `GS_TEST_HOST_PASSWORD` on Windows.
- Firewall: the client first talks to the NetLDI (e.g. port 46019), then directly to the gem
  the NetLDI started, on a further port. Either allow those ports, or restrict them with the
  configuration parameter `NETLDI_PORT_RANGE` and open just that range.
- The NetLDI should run with `LC_NUMERIC=C` (see "Known GemStone issues" in the README).

### Running the tests

```powershell
git clone https://github.com/feldti/gemstone-csharp.git
cd gemstone-csharp

$env:GEMSTONE = "C:\GemStone\GemStone64BitClient3.7.5-x86.Windows_NT"   # the client directory
$env:GS_TEST_STONE = "gs64stone"
$env:GS_TEST_GEM_SERVICE = "!@linuxhost#netldi:gs64ldi!gemnetobject"   # host name or IP of the server
$env:GS_TEST_USER = "DataCurator"
$env:GS_TEST_PASSWORD = "swordfish"

dotnet test     # on Windows: net10.0, net8.0 and net48, one after another
```

A named NetLDI service (`netldi:gs64ldi`) must also be known on Windows (`services` file);
a port number (`netldi:46019`) works without that.

### Further checks worth doing on Windows

1. **gsrepl** (`dotnet publish samples/GsRepl -r win-x64`, also possible from Linux): umlauts in
   the console (the sample switches the console to UTF-8), Ctrl+C sends a soft break.
2. **Running without GEMSTONE**: copy the DLLs of the client `bin` directory next to the
   program and unset `GEMSTONE`. Which DLLs `libgcirpc` really needs on Windows is not
   verified yet; possibly also the Visual C++ runtime.
3. **A small WinForms app on .NET Framework 4.8**, platform target x64, using the NuGet package
   from a local folder (see the README): login, `Evaluate`, `Load`/`SaveChanges`, and an
   `await session.EvaluateAsync(…)` from a button click, so the UI stays responsive.
4. **A 32-bit process** (platform target x86 or "Prefer 32-bit"): must fail at login with a
   clear `PlatformNotSupportedException`, not with a `BadImageFormatException`.

Please report the output of failing tests together with the GemStone and Windows versions.

## Linux arm64 (Raspberry Pi)

GemTalk supports GemStone/S 64 Bit 3.7 on Linux arm64 for **development use only**; 3.7.1
added preliminary support for the Raspberry Pi. Before 3.7, there are no arm64 client
libraries.

### What you need

| What | Notes |
|---|---|
| Raspberry Pi 4 or 5 (8 GB recommended for a stone; a client alone needs much less) | Pi 3 works as a pure client |
| A **64-bit** OS: Raspberry Pi OS (64-bit) or Ubuntu Server arm64 | `uname -m` must print `aarch64`; a 32-bit OS cannot load the library |
| GemStone/S 64 Bit 3.7.x for Linux arm64 | From GemTalk. Either a whole server on the Pi, or just its client library for a stone elsewhere, **same version as the stone** |
| .NET 8 or .NET 10 SDK for arm64 | e.g. with `dotnet-install.sh` (https://dot.net/v1/dotnet-install.sh) |

.NET Framework 4.8 (net48) is Windows-only; Mono on arm64 is not supported by this project.

### Running the tests

```bash
git clone https://github.com/feldti/gemstone-csharp.git && cd gemstone-csharp
export GEMSTONE=/opt/gemstone/GemStone64Bit3.7.x-arm64.Linux     # the arm64 product directory
export GS_TEST_STONE=gs64stone GS_TEST_GEM_SERVICE='!@localhost#netldi:gs64ldi!gemnetobject'
export GS_TEST_USER=DataCurator GS_TEST_PASSWORD=swordfish
dotnet test -f net8.0      # or net10.0, depending on the installed SDK/runtime
```

Or without an SDK on the Pi: publish the console on another machine and copy it over:

```bash
dotnet publish samples/GsRepl -c Release -r linux-arm64
scp samples/GsRepl/bin/Release/net10.0/linux-arm64/publish/gsrepl pi@raspberrypi:
ssh pi@raspberrypi 'GEMSTONE=/opt/gemstone/… GS_GEM_SERVICE=… GS_PASSWORD=… ./gsrepl -e "3 + 4"'
```

### What could differ on arm64

The wrapper has no x64-specific code: both platforms are 64-bit little-endian with the same
struct layout, and the GCI functions it calls are not variadic, so there is one calling
convention. Worth watching:

- the file name of the client library (the wrapper looks for `libgcirpc-*-64.so` in `$GEMSTONE/lib`);
- which other libraries it loads at runtime (on x86-64: `libssl` and `libkrb5`);
- floating point: the tests `SmallDoubleEncodesLocallyLikeGciFltToOop` and the Float cases of
  `ScalarValuesSurviveABatchedSave` compare the local encoding with the server's;
- performance on a Pi: `SessionsExecuteInParallel` expects two parallel one-second waits to
  finish within 0.95–1.7 s, which may be tight on a busy Pi.
