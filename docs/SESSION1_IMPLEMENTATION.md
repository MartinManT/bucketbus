# Phase 1A Session 1 implementation results

Status: IMPLEMENTED for review · 2026-10-21 · T01–T03 only

The user explicitly authorized Session 1 and accepted the engineering GO assessment. Session 2 and later work remain unapproved. No commit, push, live Ceph access or infrastructure provisioning was performed.

## Delivered behavior

A buildable net10.0 solution contains the four approved production projects and two offline test projects. The Avalonia shell presents local/remote panes, an adjustable splitter, read-only location fields, an empty transfer area, startup progress and a safe error state. Tab/Shift+Tab, Ctrl+Tab and Ctrl+L have executable headless coverage. No connection, filesystem browsing, transfer, credential-store or SQLite functionality is present.

Desktop owns Avalonia, MVVM, logging and the explicit DI composition root. Core contains an immutable initialization-state model with a guard against reporting failed initialization as ready. S3 references Core and the AWS SDK package but constructs no client. Platform references Core and has no service implementation yet. Empty future interfaces and marker classes were avoided.

## Created file groups

| Area | Files / purpose |
| --- | --- |
| Solution/toolchain | `BucketBus.slnx`, `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `NuGet.Config`; nullable, warnings as errors, deterministic builds, exact package ranges and audited restores |
| Production | Four project files and lockfiles; Core initialization model; Desktop entry point, application resources, composition root, view model, shell XAML/focus handling and offline design data |
| Tests | Core state/boundary tests; Desktop headless/resource/focus/binding/diagnostic tests; shared test settings and lockfiles |
| Developer guidance | `README.md`, `AGENTS.md`, `eng/validate.sh`, build-output ignores and this record |
| Dependencies | [Resolved inventory and license disposition](DEPENDENCIES.md); raw machine-specific audit, graph and execution records remain in ignored local evidence |

## Verified versions

| Component | Actual result |
| --- | --- |
| SDK / runtime | SDK **10.0.113**, MSBuild **18.0.36**, runtime **10.0.13**; local installation verified against Microsoft's published SHA-512. Existing system SDK was left in place. |
| Avalonia / Fluent / Headless.XUnit | **12.1.4**, restored and compiled together |
| CommunityToolkit.Mvvm | **8.4.2**, used by the shell |
| Microsoft DI / Logging / Logging.Debug | **10.0.13**, composed and exercised in tests |
| AWSSDK.S3 / Core | **4.0.104.5 / 4.0.102.12**, net10.0 dependency resolution verified; provider behavior untested |
| xunit.v3 / VS adapter / Test SDK | **3.2.2 / 3.1.5 / 18.11.0**, restored and passing with Avalonia's xUnit v3 adapter; the selected SDK reports VSTest CLI 18.0.2 |

No proposed direct version required substitution. The union of resolved graphs contains **59 package/version pairs**. License declarations and the ANGLE native license file were inspected; NuGet audit found **no known advisories** in direct or transitive packages on the recorded date. This does not establish an absence of undisclosed vulnerabilities or complete native-binary redistribution notices.

Avalonia.Templates was not installed: direct bootstrap was sufficient. Inter is present transitively through Avalonia. The proposed Linux credential binding and SQLite were not added directly. Build/test telemetry components found transitively are documented in the inventory; the validation script uses their documented opt-outs. This tooling is separate from BucketBus application behavior.

## Fedora validation

Executed on **Fedora Linux 44 Server Edition, x64**, with the locally installed Microsoft linux-x64 SDK:

| Check | Result | Evidence / limitation |
| --- | --- | --- |
| Official SDK download hash | PASS | SHA-512 matched release metadata; no unrelated system upgrade |
| Initial restore and locked restore | PASS | Six projects; project lockfiles match central requirements |
| Release solution build | PASS | Zero warnings, zero errors |
| Core offline tests | PASS | **9 passed**, zero failed/skipped: failure/ready invariants and allowed project/framework dependencies |
| Desktop offline tests | PASS | **6 passed**, zero failed/skipped: real application resources, validated DI, window initialization, bindings, focus shortcuts/traversal, safe failure diagnostics and design data |
| NuGet audit | PASS for dated feed | Six project graphs checked with transitive packages; no known advisories or audit warnings reported |
| Native application launch attempt | BLOCKED for interactive validation | Process exited 1 with fixed `BB-STARTUP` output. No display is advertised and Xvfb is absent; no window/rendering success claimed. |
| Windows execution / packaging | NOT RUN | Requires the planned Windows environment; no inference from Fedora/headless results |
| Live provider / credentials | NOT RUN | Intentionally outside Session 1; no live harness exists |

`eng/validate.sh` passed after the final build/test configuration changes: locked restore, Release build and **15 tests total**, zero failures/skips. Restore/audit use NuGet; the tests use no provider, credential or network service. No network-isolation experiment was performed, and headless tests do not prove native rendering or accessibility.

To repeat with an SDK installed outside the normal toolchain path, set `DOTNET_ROOT` and prepend it to `PATH`, then run `./eng/validate.sh`. Current-machine paths and raw logs are kept locally, not in public project defaults.

## Outstanding work and Session 2 prerequisites

- Session 2 requires separate user approval for **T04/T05 only**: profiles, explicit credential binding and secure/session-only credential paths. No speculative contracts or storage were introduced here.
- Actual Linux GUI/Secret Service validation needs an interactive user desktop and available service. This server shell's advertised D-Bus address is not proof of vault availability.
- Windows Credential Manager, portable ZIP execution, DPI and Narrator/NVDA checks still need the planned Windows 11 VM. Session 1 did not provision it.
- S3 SDK wire behavior, exact-key/pagination handling and live qualification begin in their later approved sessions. Package resolution alone establishes no AWS/Ceph compatibility.
- Native dependency notices, published artifact auditing, performance budgets and full platform qualification retain their roadmap gates.

Architecture B and approved ADR-006 remain binding. SQLite selection/durability and write-replay safety remain later implementation gates. This session stops at the offline foundation and shell.
