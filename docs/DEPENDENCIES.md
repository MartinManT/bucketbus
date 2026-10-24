# Dependency inventory — Sessions 1–6

Reverified on 2026-10-22 through an actual .NET 10 locked restore/build and NuGet audit. Session 3 adds an SDK test project and its lockfile, with no package-version changes; eight project lockfiles now record the graphs. Session 4 reuses this exact package set and re-runs the audit; no direct or transitive version changes are introduced. This inventory covers the selected net10.0 graphs, including build/test and platform-native assets. Presence of native assets is not platform execution evidence.

Exact direct requirements are centralized in `Directory.Packages.props`; each project lockfile records resolved versions and content hashes. The union contains **60 package/version pairs**. NuGet reported **no known vulnerabilities**, including transitive packages, at this check; this is a dated feed result, not a guarantee that dependencies are vulnerability-free.

## Licensing disposition

Package metadata declares MIT or Apache-2.0, except the ANGLE Windows native package, whose included LICENSE was inspected and contains BSD-style three-clause terms. These permit the intended Apache-2.0 project use with their required notices. Retain copyright/license notices and any bundled third-party notices when preparing distributed artifacts. No session has produced a published release. Session 6 aggregates restored package/runtime notices and vendored upstream license texts into the Windows evaluation ZIP; final exact-source notice/provenance review remains a public-release prerequisite. This inventory records technical license review, not a legal certification.

Avalonia.BuildServices is a build-only transitive dependency. The validation script sets its documented telemetry opt-out. xUnit v3 also brings Microsoft Testing Platform/telemetry packages even though this solution selects VSTest; build/test opt-outs are explicit. No application telemetry is configured. The platform Tmds.DBus.Protocol package below is transitively used by Avalonia, not the BucketBus credential-store binding. Session 2 adds the separate Tmds.DBus 0.95.1 package to Platform; its net6.0 asset has no package dependencies. Its MIT metadata and restored graph were verified, and all original versions remain unchanged.

## Resolved packages

| Package | Version | Declared license / inspected file |
| --- | --- | --- |
| AWSSDK.Core | 4.0.102.12 | Apache-2.0 |
| AWSSDK.S3 | 4.0.104.5 | Apache-2.0 |
| Avalonia | 12.1.4 | MIT |
| Avalonia.Angle.Windows.Natives | 2.1.27548.20260419 | BSD-style three-clause (included LICENSE inspected) |
| Avalonia.BuildServices | 11.3.2 | MIT |
| Avalonia.Desktop | 12.1.4 | MIT |
| Avalonia.Fonts.Inter | 12.1.4 | MIT |
| Avalonia.FreeDesktop | 12.1.4 | MIT |
| Avalonia.FreeDesktop.AtSpi | 12.1.4 | MIT |
| Avalonia.HarfBuzz | 12.1.4 | MIT |
| Avalonia.Headless | 12.1.4 | MIT |
| Avalonia.Headless.XUnit | 12.1.4 | MIT |
| Avalonia.Native | 12.1.4 | MIT |
| Avalonia.Remote.Protocol | 12.1.4 | MIT |
| Avalonia.Skia | 12.1.4 | MIT |
| Avalonia.Themes.Fluent | 12.1.4 | MIT |
| Avalonia.Win32 | 12.1.4 | MIT |
| Avalonia.X11 | 12.1.4 | MIT |
| CommunityToolkit.Mvvm | 8.4.2 | MIT |
| HarfBuzzSharp | 8.3.1.3 | MIT |
| HarfBuzzSharp.NativeAssets.Linux | 8.3.1.3 | MIT |
| HarfBuzzSharp.NativeAssets.WebAssembly | 8.3.1.3 | MIT |
| HarfBuzzSharp.NativeAssets.Win32 | 8.3.1.3 | MIT |
| HarfBuzzSharp.NativeAssets.macOS | 8.3.1.3 | MIT |
| MicroCom.Runtime | 0.11.6 | MIT |
| Microsoft.ApplicationInsights | 2.23.0 | MIT |
| Microsoft.Bcl.AsyncInterfaces | 6.0.0 | MIT |
| Microsoft.CodeCoverage | 18.11.0 | MIT |
| Microsoft.Extensions.DependencyInjection | 10.0.13 | MIT |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.13 | MIT |
| Microsoft.Extensions.Logging | 10.0.13 | MIT |
| Microsoft.Extensions.Logging.Abstractions | 10.0.13 | MIT |
| Microsoft.Extensions.Logging.Debug | 10.0.13 | MIT |
| Microsoft.Extensions.Options | 10.0.13 | MIT |
| Microsoft.Extensions.Primitives | 10.0.13 | MIT |
| Microsoft.NET.Test.Sdk | 18.11.0 | MIT |
| Microsoft.TestPlatform.ObjectModel | 18.11.0 | MIT |
| Microsoft.TestPlatform.TestHost | 18.11.0 | MIT |
| Microsoft.Testing.Extensions.Telemetry | 1.9.1 | MIT |
| Microsoft.Testing.Extensions.TrxReport.Abstractions | 1.9.1 | MIT |
| Microsoft.Testing.Platform | 1.9.1 | MIT |
| Microsoft.Testing.Platform.MSBuild | 1.9.1 | MIT |
| Microsoft.Win32.Registry | 5.0.0 | MIT |
| SkiaSharp | 3.119.4 | MIT |
| SkiaSharp.NativeAssets.Linux | 3.119.4 | MIT |
| SkiaSharp.NativeAssets.WebAssembly | 3.119.4 | MIT |
| SkiaSharp.NativeAssets.Win32 | 3.119.4 | MIT |
| SkiaSharp.NativeAssets.macOS | 3.119.4 | MIT |
| Tmds.DBus | 0.95.1 | MIT |
| Tmds.DBus.Protocol | 0.94.1 | MIT |
| xunit.analyzers | 1.27.0 | Apache-2.0 |
| xunit.runner.visualstudio | 3.1.5 | Apache-2.0 |
| xunit.v3 | 3.2.2 | Apache-2.0 |
| xunit.v3.assert | 3.2.2 | Apache-2.0 |
| xunit.v3.common | 3.2.2 | Apache-2.0 |
| xunit.v3.core.mtp-v1 | 3.2.2 | Apache-2.0 |
| xunit.v3.extensibility.core | 3.2.2 | Apache-2.0 |
| xunit.v3.mtp-v1 | 3.2.2 | Apache-2.0 |
| xunit.v3.runner.common | 3.2.2 | Apache-2.0 |
| xunit.v3.runner.inproc.console | 3.2.2 | Apache-2.0 |

## Sources and repeatable checks

- [NuGet audit documentation](https://learn.microsoft.com/en-us/nuget/concepts/auditing-packages) and [package-list command](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-package-list).
- [Avalonia build telemetry and opt-out](https://github.com/AvaloniaUI/Avalonia.BuildServices).
- [Microsoft Testing Platform telemetry opt-out](https://learn.microsoft.com/en-us/dotnet/core/testing/microsoft-testing-platform-telemetry).
- Package metadata and included license files were read from the packages restored from nuget.org, rather than inferred from product names.

```bash
dotnet package list --project BucketBus.slnx --include-transitive --vulnerable --no-restore
dotnet package list --project BucketBus.slnx --include-transitive --no-restore
```

Session 2 adds only the Tmds.DBus credential binding. No Microsoft.Data.Sqlite, ORM or other future-feature dependency was added directly. Avalonia.Templates was unnecessary because the minimal application was bootstrapped directly. Avalonia.Fonts.Inter is present transitively even though the shell adds no explicit font package.

Session 5 adds a separate integration-test project using existing dependencies: nine ordinary lockfiles still resolve the same 60 package/version pairs. Four `packages.linux-x64.lock.json` files retain the RID-specific production graphs without replacing ordinary locks; their graph union contains 36 package/version pairs. Self-contained publish includes .NET 10.0.13 runtime assets. Ordinary and integration NuGet audits report no known vulnerabilities on 2026-10-22. No direct/transitive package versions were changed. Release distribution notice aggregation remains pending; the internal evaluation artifact is not release qualification.

Session 6 adds four `packages.win-x64.lock.json` production lockfiles with the same 36 package/version pairs as the Linux RID graph. All nine ordinary and four Linux lockfiles were preserved byte-for-byte. SDK 10.0.113 cross-publishes bundled Windows runtime 10.0.13; no application package/version changes were made. `eng/publish-windows.sh` requires the exact SDK and `eng/package-windows.py` validates the runtime/native payload, copies available package license/notice texts and supplements packages lacking text from [vendored upstream notices](third-party/README.md). The resulting dependency inventory conservatively includes 36 restored packages plus the runtime pack. AWS upstream notices are a dated snapshot with exact-source provenance still requiring public-release review. Native Windows compatibility remains BLOCKED.
