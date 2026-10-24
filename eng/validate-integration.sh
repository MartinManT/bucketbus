#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1 AVALONIA_TELEMETRY_OPTOUT=1 TESTINGPLATFORM_TELEMETRY_OPTOUT=1 DOTNET_GENERATE_ASPNET_CERTIFICATE=false
dotnet restore BucketBus.Integration.slnx --locked-mode
dotnet build BucketBus.Integration.slnx -c Release --no-restore
# Guard suite only: cannot run live tests even if the caller has live opt-in set.
dotnet test BucketBus.Integration.slnx -c Release --no-build --no-restore --filter 'Category!=LiveS3'
