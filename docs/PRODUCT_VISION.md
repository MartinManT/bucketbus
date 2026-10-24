# BucketBus product vision

Status: DRAFT for review · 2026-10-21

## Purpose and document contract

BucketBus makes routine local-to-object-storage file transfers understandable, predictable, and recoverable in a desktop Commander interface. It is a general-purpose S3-compatible client with first-class Ceph RGW validation, not a Ceph-only console.

This document set records planning, not implemented behavior. **Confirmed** means a user-established requirement or inventory fact. **Approved** identifies an explicitly accepted design decision, including architecture B and ADR-006's recovery scope. **Proposed** identifies engineering choices still awaiting review, including the preferred SQLite journal. **Unresolved validation requirement** means evidence or a test environment is still needed. **Documented** means an upstream source describes an interface; this does not establish BucketBus compatibility. **Executed test evidence** requires a recorded run; Session 1 now has offline foundation/shell evidence; no SDK provider qualification has been performed. Local connectivity records are maintained separately and do not establish application compatibility. An individual decision can be approved while these documents remain drafts.

Read [PRD](PRD.md) for scope and acceptance criteria, [architecture](ARCHITECTURE.md) for boundaries, [security](SECURITY_MODEL.md) for protections, [Ceph compatibility](CEPH_COMPATIBILITY.md) for API evidence, [UX](UX_SPECIFICATION.md) for flows, [tests](TEST_STRATEGY.md) for validation, [roadmap](ROADMAP.md) for gates, and [decisions](DECISIONS.md) for status and risks. Decisions are authoritative for approval status; PRD is authoritative for MVP scope. Conflicts must be resolved explicitly, not by silently widening scope.

## People and initial problem

Confirmed initial users are IT professionals: Linux administrators, storage administrators, and infrastructure engineers who work with Ceph RGW or other S3 endpoints from Windows workstations. Less technical colleagues should be able to use the same basic workflow without understanding AWS CLI.

The product hypothesis is that routine CLI-based transfers can create avoidable friction for desktop users. Backups, archives, logs, and ISO images are illustrative workloads, not validated research. No interviews, usability measurements, procurement approvals, or competitor trials have occurred.

The first useful experience is: extract the application, connect to an allowed bucket, locate a file, copy it, see whether it actually finished, and recover from failure without guessing what happened. A connection with access to one bucket must work even when listing all buckets is forbidden.

## Product promises

- Windows 11 x64 ZIP distribution: no installer, elevation, mandatory service, or separately installed .NET runtime. Per-user settings and OS-protected credentials are acceptable.
- A shared Windows/Linux codebase. Linux development and testing begin early; published Linux support is limited to tested distributions and desktop configurations.
- Ceph first in development; actual Ceph and AWS S3 evidence required for public v1.0 compatibility claims.
- Apache License 2.0 project licensing, transparent dependencies, and reviewable release provenance. Open source enables auditing; it does not itself guarantee corporate approval or security.
- No telemetry, automatic crash upload, or unsolicited network activity by default. Storage connections occur through explicit user workflows.
- Ordinary transfers never need Ceph administrative credentials.

## Competitive assessment

Research checked on 2026-10-21. These are documented capabilities, not comparative test results.

| Product | Established overlap | Implication for BucketBus |
| --- | --- | --- |
| WinSCP | Open-source Windows client; Commander UI, S3, and portable operation are already available. Custom S3 endpoints are documented. | The closest baseline. Neither dual panes nor open source alone differentiates BucketBus. Compare the exact restricted-bucket workflow before claiming better usability. |
| S3 Browser, now CS Browser | Windows client with S3-compatible accounts, transfers, permissions, and extensive bucket tools. | Competing on feature count would inflate scope. Investigate the operator's most frequent workflow and trust requirements instead. |
| Cyberduck | Open-source Windows/macOS browser with S3, bookmarks, transfers, synchronization, and OS integration. | Simplicity and secure credential handling are existing expectations. BucketBus must demonstrate the value of its two-pane flow and Linux path. |
| Rclone Browser | Open-source cross-platform desktop frontend to rclone; remote browsing, background jobs, and portable mode. | Useful comparator for Linux and transfer queues. A bundled external engine is an alternative, but adds another configuration and process lifecycle to own. |
| rclone web GUI | Browser UI for rclone operations. | A useful workflow comparator, although the requested product is a desktop UI. |

Sources: [WinSCP introduction](https://winscp.net/eng/docs/introduction), [WinSCP S3](https://winscp.net/eng/docs/s3), [CS Browser](https://s3browser.com/), [Cyberduck](https://cyberduck.io/), [Rclone Browser](https://github.com/kapitainsky/RcloneBrowser), [rclone GUI](https://rclone.org/gui/).

**Differentiation hypothesis:** a focused, consistent Windows/Linux Commander workflow, honest transfer outcomes, explicit restricted-bucket support, and published Ceph interoperability evidence can earn adoption. These are goals to prove, not unique features already established by research. Future administration provides a separate reason for storage teams to adopt the product; it must not justify an oversized MVP.

## Non-goals

No replacement for Ceph Dashboard, cluster orchestration, SSH terminal, filesystem mount, backup scheduler, general FTP/SFTP suite, online collaboration service, or complete AWS console. No effective-permissions calculator. No runtime plugin marketplace. No automatic synchronization or remote rename in MVP. No macOS or ARM release commitment yet.

## Success and learning

Proposed formative study: five consenting users, including at least two who rarely use a CLI, perform connection, upload, download, error recovery, and deletion tasks on disposable data. Target four of five completing the basic transfer unaided, zero unintended destructive operations, and correct identification of failed/uncertain transfers. This small study guides UX; it does not establish population-wide usability.

Confirmed workload/development inputs are typical files under 1 GB, LAN and WAN/VPN networks, Squid (19.x) as the primary validation family, and Fedora/.NET 10 SDK/Docker development. ADR-006's whole-object restart model is approved; partial-byte resume across restart is deferred to Phase 2. Complete qualification planning for provider package/build provenance, configuration, isolated write-test authority, bucket types, maximum size, object counts, interruption frequency and restricted credentials. Windows 11 VM evaluation remains a Phase 1A action. Proxy/TLS interception needs remain unknown. Keep local infrastructure configuration, credential references and execution records outside public documentation; inventory does not establish test evidence.

The measurable engineering criteria are in [PRD](PRD.md). Delivery depends on passing them, not on a speculative calendar date.
