# Supplemental dependency notices

These unmodified upstream texts supplement license/notice files copied from the
exact restored NuGet packages by `eng/package-windows.py`. They are vendored so
packaging does not depend on mutable web content. Acquired 2026-10-22.

| Files | Upstream source |
| --- | --- |
| Avalonia.LICENSE.txt | [Avalonia licence.md, package repository commit](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/licence.md) |
| MicroCom.LICENSE.txt | [MicroCom LICENSE, package repository commit](https://github.com/kekekeks/MicroCom/blob/76785efcafd91b5902fd19dd11145f6dd655b7b4/LICENSE) |
| Tmds.DBus.LICENSE.txt | [Tmds.DBus COPYING, package repository commit](https://github.com/tmds/Tmds.DBus/blob/491bde2c16d65a7904397933cffa24e15e45eb2a/COPYING) |
| Tmds.DBus.Protocol.LICENSE.txt | [Tmds.DBus COPYING, protocol package repository commit](https://github.com/tmds/Tmds.DBus/blob/b4a7fed0b878f74cb54f7cca84d2889af4e596ba/COPYING) |
| AWSSDK.LICENSE.txt, AWSSDK.NOTICE.txt | [License.txt](https://github.com/aws/aws-sdk-net/blob/f9e97693cdbb3264de6a4887c8a68bb39be4b37a/License.txt), [Notice.txt](https://github.com/aws/aws-sdk-net/blob/f9e97693cdbb3264de6a4887c8a68bb39be4b37a/Notice.txt) |

AWS packages declare Apache-2.0 but contain no notice text or repository commit in
their package metadata. The AWS files above are a dated upstream snapshot, not a
claim that that commit produced the pinned SDK packages. They conservatively retain
upstream third-party acknowledgments. Match exact source provenance before a public
release. No license expression is treated as a replacement for bundled notices.

The publish workflow also copies .NET runtime, SkiaSharp, HarfBuzz, ANGLE, Inter font
and other included package notices. This is a technical inventory, not legal
certification. A complete release provenance/SBOM/signing review remains required.

Phase 1C.1 adds Microsoft.Data.Sqlite's MIT license from the restored package's
repository commit `4099f83977f4d29604a9aeb971fb0a7bc1d5f699`, path
`src/efcore/LICENSE.txt` in `dotnet/dotnet`. SQLitePCLRaw 2.1.13 package metadata
declares Apache-2.0; its upstream `ericsink/SQLitePCL.raw/main/LICENSE.TXT` text
was retrieved on 2026-10-25. The native SQLite engine's public-domain statement
is referenced in `SQLite.NOTICE.txt`. These notices accompany the new runtime
assets; package content hashes remain recorded in the lockfiles.
