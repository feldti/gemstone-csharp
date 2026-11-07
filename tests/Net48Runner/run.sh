#!/usr/bin/env bash
# Runs the test suite against the net48 build with Mono (Linux). Needs Mono with its facade
# assemblies (e.g. Debian/Ubuntu: mono-complete). Stone variables as for "dotnet test".
# Optional argument: substring filter on "Namespace.Class.Method".
set -euo pipefail
cd "$(dirname "$0")"
dotnet build -c Debug -p:WithNet48Tests=true -v q -nologo >/dev/null
exec mono bin/Debug/net48/Net48Runner.exe "$@"
