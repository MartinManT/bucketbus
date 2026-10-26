#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1 AVALONIA_TELEMETRY_OPTOUT=1 TESTINGPLATFORM_TELEMETRY_OPTOUT=1 DOTNET_GENERATE_ASPNET_CERTIFICATE=false
# Dedicated RID lockfiles preserve the ordinary solution graphs.
dotnet restore src/BucketBus.Desktop -r linux-x64 -p:NuGetLockFilePath=packages.linux-x64.lock.json --locked-mode
dotnet publish src/BucketBus.Desktop -c Release -r linux-x64 --self-contained true --no-restore -p:NuGetLockFilePath=packages.linux-x64.lock.json -o artifacts/session5/linux-x64

python3 eng/check-sqlite.py artifacts/session5/linux-x64 linux-x64
