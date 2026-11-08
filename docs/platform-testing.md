# Testing on Windows, Linux arm64 (Raspberry Pi) and macOS (Apple Silicon)

The test suite runs on Linux x86-64 against GemStone 3.6.5, 3.6.8, 3.7.2, 3.7.5 and 3.7.6, for
.NET 10, .NET 8 and (with Mono) .NET Framework 4.8.

Windows x64 as a client: all tests pass for .NET 10, .NET 8 and .NET Framework 4.8 (native),
with the GemStone 3.7.6 Windows client against a stone on Linux (Windows 11 25H2, build 26200.9711, November 2026).
The first run found a bug in the Windows client library (`GciGetFreeOops`, see the README).
Not tested yet: the further checks below (gsrepl, running without `GEMSTONE`, WinForms, 32 bit).

Linux arm64 and macOS on Apple Silicon are supported with .NET 10 only and have not been tested
yet. On arm64, the test project builds for net10.0 only.

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

On arm64, this project supports **.NET 10 only**: .NET 10 is the first version that properly
supports the Raspberry Pi. An application built for net8.0 stops at the first login with a
`PlatformNotSupportedException`. arm64 on Windows is not supported either (no GemStone client).

### What you need

| What | Notes |
|---|---|
| Raspberry Pi 4 or 5 (8 GB recommended for a stone; a client alone needs much less) | Pi 3 works as a pure client |
| A **64-bit** OS: Raspberry Pi OS (64-bit) or Ubuntu Server arm64 | `uname -m` must print `aarch64`; a 32-bit OS cannot load the library |
| GemStone/S 64 Bit 3.7.x for Linux arm64 | From GemTalk, as a zip (`linux/arm64`, unpack anywhere, no root needed; best for tests) or as a Debian package (`linux apt/arm64`, installed with `apt`, needs root). Either a whole server on the Pi, or just its client library for a stone elsewhere, **same version as the stone** |
| .NET 10 SDK for arm64 | e.g. with `dotnet-install.sh` (https://dot.net/v1/dotnet-install.sh) |

.NET 8 and .NET Framework 4.8 (net48, Windows-only) are not supported on arm64, nor is Mono.

### Running the tests

```bash
git clone https://github.com/feldti/gemstone-csharp.git && cd gemstone-csharp
export GEMSTONE=/opt/gemstone/GemStone64Bit3.7.x-arm64.Linux     # the arm64 product directory
export GS_TEST_STONE=gs64stone GS_TEST_GEM_SERVICE='!@localhost#netldi:gs64ldi!gemnetobject'
export GS_TEST_USER=DataCurator GS_TEST_PASSWORD=swordfish
dotnet test                # builds and runs net10.0 only on arm64
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

## macOS (Apple Silicon)

Unlike Windows, GemTalk ships the **whole product** for macOS (download `3.7.x/silicon`): stone,
gems and NetLDI run on the Mac itself, so the test suite runs against a local stone, as on Linux.
GemTalk supports GemStone on macOS for **development use only**. As on Linux arm64, this project supports **.NET 10 only** there.

### What you need

| What | Notes |
|---|---|
| A Mac with Apple Silicon (M1 or later) | macOS version as listed in GemTalk's installation guide for the Mac |
| GemStone/S 64 Bit 3.7.x for Mac (`silicon`) | Install it as described in GemTalk's installation guide (*Installing GemStone/S 64 Bit on Mac*); it also covers the shared memory settings for the stone |
| .NET 10 SDK for Arm64 (macOS installer) | https://dotnet.microsoft.com/download |
| Git | e.g. `xcode-select --install` |

### Running the tests

Create and start a stone and a NetLDI as the installation guide describes. Start the NetLDI with
`LC_NUMERIC=C` and in guest mode, like the Linux test stones:

```bash
export GEMSTONE=/path/to/GemStone64Bit3.7.6-arm64.Darwin   # the product directory
export PATH=$GEMSTONE/bin:$PATH
startstone gs64stone
LC_NUMERIC=C startnetldi -g -a $USER gs64ldi

git clone https://github.com/feldti/gemstone-csharp.git && cd gemstone-csharp
export GS_TEST_STONE=gs64stone GS_TEST_GEM_SERVICE='!@localhost#netldi:gs64ldi!gemnetobject'
export GS_TEST_USER=DataCurator GS_TEST_PASSWORD=swordfish
dotnet test                # net10.0 only on arm64
```

The directory name above is an example; use the one from the download.

### What could differ on macOS

- **The library file:** the wrapper looks for `libgcirpc-*-64.dylib` in `$GEMSTONE/lib`. If the
  Mac product names or places it differently, the tests fail at the first login with "library
  not found"; please report the actual name.
- **Libraries that `libgcirpc` loads itself** (on Linux: `libssl`, `libkrb5`): the wrapper loads
  `libgcirpc` by its absolute path, but how it finds its own dependencies on macOS is not
  verified.
- **Quarantine:** files downloaded with a browser carry the quarantine attribute, and macOS may
  refuse to load the libraries ("cannot be opened because the developer cannot be verified").
  Removing it from the product directory helps: `xattr -dr com.apple.quarantine $GEMSTONE`.
- **gsrepl:** on Apple Silicon every executable must be signed, at least ad hoc. Best publish it
  on the Mac itself (`dotnet publish samples/GsRepl -c Release -r osx-arm64`). If it was built
  elsewhere and does not start, sign it ad hoc: `codesign --force -s - gsrepl`.
- **`SessionsExecuteInParallel`** measures time and may be tight on a busy machine, as on the Pi.
