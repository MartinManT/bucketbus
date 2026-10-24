#!/usr/bin/env bash
set -euo pipefail

cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.."

# These are tool opt-outs, independent of BucketBus application behavior.
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export AVALONIA_TELEMETRY_OPTOUT=1
export TESTINGPLATFORM_TELEMETRY_OPTOUT=1
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false

dotnet restore BucketBus.slnx --locked-mode
dotnet build BucketBus.slnx --configuration Release --no-restore
dotnet test BucketBus.slnx --configuration Release --no-build --no-restore
