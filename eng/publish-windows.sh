#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1 AVALONIA_TELEMETRY_OPTOUT=1 TESTINGPLATFORM_TELEMETRY_OPTOUT=1 DOTNET_GENERATE_ASPNET_CERTIFICATE=false

# This is artifact construction on Fedora, not native Windows qualification.
# Require the tested SDK exactly; a servicing update needs new evidence.
expected_sdk=$(python3 -c 'import json; print(json.load(open("global.json"))["sdk"]["version"])')
if [[ "$(dotnet --version)" != "$expected_sdk" ]]; then
    echo "Install the SDK pinned in global.json before publishing." >&2
    exit 1
fi

mkdir -p artifacts/session6
publish_stage=$(mktemp -d artifacts/session6/.windows-publish.XXXXXXXX)
trap 'rm -rf -- "$publish_stage"' EXIT

dotnet restore src/BucketBus.Desktop -r win-x64 --locked-mode \
    -p:NuGetLockFilePath=packages.win-x64.lock.json
dotnet publish src/BucketBus.Desktop -c Release -r win-x64 --self-contained true --no-restore \
    -p:NuGetLockFilePath=packages.win-x64.lock.json \
    -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishAot=false \
    -p:DebugType=None -p:DebugSymbols=false -o "$publish_stage/BucketBus"

# Only the fresh publish payload and explicitly selected public documents enter the ZIP.
python3 eng/package-windows.py "$publish_stage/BucketBus"
