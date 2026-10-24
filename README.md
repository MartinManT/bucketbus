# BucketBus

A cross-platform, Commander-style S3 file manager, with Ceph RGW as the initial validation target.

Development is at **Phase 1A, Session 6**. The desktop provides profile/credential management and a read-only S3 browser: explicit connect/disconnect, bucket and prefix navigation, bounded paging and on-demand metadata. Local browsing and transfers remain placeholders. See [Session 4 results](docs/SESSION4_IMPLEMENTATION.md) for executed offline/headless evidence and limits. Initial SDK reads against one identified Ceph deployment now pass; native Linux qualification is partial, and Windows/AWS qualification remains pending. See [Session 5 evidence](docs/SESSION5_IMPLEMENTATION.md). Session 6 adds a Windows x64 self-contained evaluation ZIP cross-published on Fedora; [Windows native validation](docs/WINDOWS_VALIDATION.md) is BLOCKED until the VM is available. See [Session 6 evidence](docs/SESSION6_IMPLEMENTATION.md).

## Build and test

Install the .NET SDK selected by [global.json](global.json) (10.0.113, or a servicing patch in the same feature band). Package versions are centralized and the resolved graphs are recorded in project lockfiles.

For a custom SDK installation, set `DOTNET_ROOT` to its installation directory and prepend that directory to `PATH` before using the commands below. The system SDK is not upgraded by this repository.

```bash
./eng/validate.sh
```

The script performs a locked restore, Release build and offline test run. It opts out of .NET, Avalonia build and Microsoft Testing Platform telemetry. For manual commands, set `DOTNET_CLI_TELEMETRY_OPTOUT=1`, `AVALONIA_TELEMETRY_OPTOUT=1` and `TESTINGPLATFORM_TELEMETRY_OPTOUT=1` first. Tests also load the repository's opt-out runsettings by default. Restore and dependency auditing require NuGet access; application tests themselves use no external services.

Default tests run offline after restore. They use temporary files, fake vault boundaries and synthetic SDK transports, never external S3 services or real credential stores, and need no graphical display. A TLS rejection fixture opens an ephemeral loopback listener with a disposable self-signed certificate; it makes no external connections or trust-store changes. The native vault test skips unless explicitly opted in. Avalonia headless tests exercise shell/editor initialization, bindings and focus, and do not establish native desktop or screen-reader compatibility.

To run on a graphical desktop:

```bash
dotnet run --project src/BucketBus.Desktop --configuration Release --no-build
```

The shell supports Tab/Shift+Tab focus traversal, Ctrl+Tab to switch panes and Ctrl+L to focus the active pane's read-only location. In the remote list, use arrows to select, Enter to open a bucket/prefix, Backspace to go up, Alt+Enter for properties and Ctrl+R to refresh. Bucket/prefix inputs accept Enter to navigate. Backspace in text inputs edits text. Native desktop execution requires an available display and Avalonia's platform prerequisites; see [supported platforms](https://docs.avaloniaui.net/docs/supported-platforms).

## Structure

- **Desktop**: Avalonia views/view models and the DI composition root.
- **Core**: framework-independent profiles, credentials, read-only storage contracts and session lifecycle workflows.
- **S3**: explicit AWS SDK v4 clients, bounded read-only requests and protocol normalization.
- **Platform**: atomic JSON profiles, Windows Credential Manager and Linux Secret Service adapters.

S3 and Platform depend only on Core. Desktop composes the modules. The [implementation handoff](docs/PHASE1A_PLAN.md), [architecture](docs/ARCHITECTURE.md) and [security model](docs/SECURITY_MODEL.md) govern further work.

Use **Profiles** to create or edit an HTTPS endpoint, signing region, addressing style and optional bucket/prefix. Choose `SessionOnly` to keep credentials in memory or `SecureStore` to use the OS vault. Temporary session tokens require `SessionOnly`. Changing endpoint, region or addressing style detaches the old binding unless you explicitly supply replacement credentials. Profile files contain only metadata and opaque local references; do not treat them as portable credential exports. Session-only bindings need fresh credentials after restart.

Select a saved profile and choose **Connect**; credentials are acquired only on that action. Allow a vault unlock prompt explicitly when needed. A configured bucket is listed directly without account discovery. If discovery is denied, enter an authorized bucket and choose **Go**. Prefixes are exact S3 prefixes, including leading/repeated slashes. **Next page** reads only one page; **Previous cached page** visits retained pages without network access. At most five pages remain cached; Refresh restarts at the first page. Rows remain in server order, with no whole-bucket sort/filter or total implied. Select an object and choose **Properties** to retrieve metadata.

Environment configuration and raw local evidence belong under ignored `docs/internal/`. Never add credentials to documentation, configuration, logs or source control. A separate, explicitly opted-in GET/HEAD harness is described in [live qualification](docs/LIVE_QUALIFICATION.md). Linux self-contained publishing and manual desktop checks are described in [Fedora validation](docs/FEDORA_VALIDATION.md). Native vault opt-in procedures and limitations are in the Session 2 results.

To construct the unsigned Windows evaluation ZIP on Fedora with the exact pinned SDK and Python 3, run `./eng/publish-windows.sh`. The output, checksum and size manifest are under ignored `artifacts/session6/`. No Windows machine, live S3 access or credential store is used by publishing. Distribute the complete ZIP with its notices and follow the Windows checklist; a cross-build is not proof of native compatibility.

Licensed under [Apache License 2.0](LICENSE). Dependencies retain their own licenses; redistribution notices and dependency review are recorded with the implementation evidence.
