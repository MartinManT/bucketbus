# Ceph and provider compatibility

Status: DRAFT research matrix · 2026-10-21

**Session 5 passed scoped AWS SDK v4 read qualification against one user-identified Ceph RGW Squid 19.2.7 deployment.** [Executed results and limits](SESSION5_IMPLEMENTATION.md) cover verified-HTTPS path-style listing, pagination, unusual-key metadata and the leading-slash correction. The user also supplied a Fedora/Openbox GUI smoke test. AWS, other deployments and full native/write/recovery qualification remain pending. Squid (19.x) is the primary initial validation family. Exact package/build provenance, backports, configuration, identity model and authorized test scope must be recorded for application qualification. Local CLI connectivity checks do not establish BucketBus or .NET SDK compatibility. This target selection is not a compatibility claim for all Squid deployments. Tentacle and Squid links below are versioned documentation references, not support commitments. Distribution backports and gateway configuration may change behavior. Avoid using `/latest/` development documentation as a release contract.

Initial qualification concerns the standard S3 workflow over both LAN and WAN/VPN, with typical files under 1 GB and approved ADR-006 recovery. Preserve the separate boundaries for standard S3, legacy RGW Admin Ops, account IAM and future Dashboard API integrations. Admin Ops/IAM administration stays in Phase 3; Dashboard visibility stays in Phase 4. Selecting Squid does not enable or qualify those future modules.

The [engineering review and Phase 1A plan](PHASE1A_PLAN.md) sequences SDK v4 read-only qualification first. Verify response nullability, exact-key encoding, custom endpoints/path-style addressing and bucket/object pagination against the selected build. AWS documentation and generic Squid feature tables do not establish matching conditional-write, checksum/trailer, retry or multipart semantics. Those remain explicit 1B/1C gates; no live operations were performed by this engineering review.

## Five different permission models

| Authority | What it governs | What it does not establish |
| --- | --- | --- |
| Standard S3 client permissions | Requests to buckets/objects as a principal: list, get, put, delete and relevant management actions | Permission to administer RGW users or Ceph cluster daemons |
| S3 resource policies and ACLs | Resource-scoped grants/denies; provider-specific supported policy language and ACL/ownership rules | A universal identity database or reliable complete permission explanation |
| RGW Admin Ops capabilities | Capability-scoped administrative HTTP operations, authenticated with RGW credentials | Equivalence to S3 bucket policy or Ceph cluster authentication |
| Ceph account IAM permissions | Account users/groups/roles and delegated identity policies, where implemented | Legacy RGW UID semantics or unrestricted cross-account/cluster administration |
| Ceph cluster administration | Cephx/cluster tooling and separately authorized manager/Dashboard operations | Automatic entitlement from possession of an S3 access key |

Ceph explicitly distinguishes RGW end users from the gateway's own cluster identity. [Ceph administration guide](https://docs.ceph.com/en/tentacle/radosgw/admin/)

Squid introduced the account IAM API for a subset of IAM operations. Account root users can delegate management through policies. Account resource ownership and quota accounting differ from legacy per-user ownership; tenants are a separate namespace dimension. Never flatten account ID, tenant, UID and IAM principal into one “user” field. [IAM](https://docs.ceph.com/en/tentacle/radosgw/iam/), [accounts](https://docs.ceph.com/en/tentacle/radosgw/account/)

Earlier releases already had roles/STS concepts. “Account IAM starts in Squid” does **not** mean “all roles and STS start in Squid.” [Reef roles](https://docs.ceph.com/en/reef/radosgw/role/), [Ceph STS](https://docs.ceph.com/en/tentacle/radosgw/STS/)

## Classification and evidence

- **Supported (documented):** a cited API describes the operation. Still unvalidated by BucketBus.
- **Conditional:** authorization, configuration, ownership, or a separate service is needed.
- **Version-dependent:** release/build-specific contracts need qualification.
- **Experimental:** incomplete public contract or insufficient evidence for a product promise.
- **Not feasible through proposed interfaces:** no suitable supported contract established for S3 + RGW Admin Ops + IAM. This is not a claim that no HTTP route can ever exist.

## Standard S3 feature matrix

| Feature | AWS general-purpose S3 | Ceph RGW | Other S3 implementations | Product phase |
| --- | --- | --- | --- | --- |
| List/read/write/delete objects, paginated prefixes | Documented | Documented | Unknown until tested | 1 |
| Multipart transfer | Documented | Documented | Unknown until tested | 1 |
| Path/virtual-hosted addressing | Endpoint constraints apply | DNS/configuration-dependent | Provider-dependent | 1 |
| Conditional writes and advanced checksums | Documented, operation-specific | Version/build/encoding-dependent | Unknown | 1 protection/integrity boundary |
| Policies, ACLs, lifecycle, versioning | Documented, permissions/ownership matter | Documented subset | Unknown | 2 management |
| Storage classes and server-side encryption | Service-specific | Placement/key-management configuration matters | Unknown | 2 selection |
| Account IAM and STS | Separate AWS services | RGW subsets/configuration | Not inferred from S3 compatibility | 2 authentication / 3 admin |
| RGW Admin Ops | Not an AWS S3 feature | Separate administrative API | Not applicable unless expressly implemented | 3 |

Ceph documents object operations, multipart, lifecycle, versioning, ACLs and storage classes, with differences such as canned ACLs and partial replication support. A feature-list entry is not a guarantee of identical AWS behavior. [Ceph S3 feature support](https://docs.ceph.com/en/tentacle/radosgw/s3/)

AWS new buckets default to owner-enforced ownership with ACLs disabled. Uploads should not send a canned ACL by default. [AWS ownership](https://docs.aws.amazon.com/AmazonS3/latest/userguide/about-object-ownership.html)

## Future administration feasibility

`{admin}` below is a configurable RGW administrative path, not necessarily `/admin`. Exact methods, schemas, capability names, and pagination must be qualified per release. The following compact Admin Ops rows summarize its documented surface. [Admin Ops reference](https://docs.ceph.com/en/tentacle/radosgw/adminops/)

| Feature | Proposed HTTP surface | Classification / caveat |
| --- | --- | --- |
| Read/create/modify/suspend/reactivate legacy RGW users | `{admin}/user` | Supported; capability-controlled |
| Create/remove access keys | `{admin}/user?key` | Supported; rotation is a multi-step workflow |
| Read/change admin capabilities | `{admin}/user?caps` | Supported; privileged change |
| Bucket statistics and ownership | `{admin}/bucket` | Supported; observation may lag |
| User/per-user bucket quotas | `{admin}/user?quota` | Supported; scope must be explicit |
| Individual bucket quota | `{admin}/bucket?quota` | Supported; distinct from user defaults |
| Account quotas | `{admin}/account?quota` | Version-dependent |
| Usage history | `{admin}/usage` | Conditional; usage logging must be enabled |
| Rate limits | `{admin}/ratelimit` | Version-dependent; verify capabilities/units |
| Bucket linking | `{admin}/bucket` | Supported; not proven equivalent to full ownership migration |
| Endpoint information | `{admin}/info` | Conditional; not complete cluster diagnostics |

**Legacy user enumeration:** Ceph's maintained Go binding calls `{admin}/metadata/user`. This is primary-source implementation evidence, not a sufficiently specified pagination/security contract. Classify as conditional/version-dependent; validate least privilege, tenant behavior, complete enumeration and page limits before promising “list users.” Do not fall back to cluster-wide metadata access silently. [Ceph Go binding](https://raw.githubusercontent.com/ceph/go-ceph/master/rgw/admin/user.go)

**Bucket policy/ACL/versioning/lifecycle editing:** standard S3 APIs, with the principal's relevant permission; these do not require Admin Ops merely because the endpoint is Ceph. Policy JSON support is a subset and release-sensitive. The bucket-policy page still includes a statement about identity policies that conflicts with newer account-IAM documentation. Treat that as a documentation inconsistency to resolve against the selected build, not as evidence that IAM policy APIs do not exist. [Ceph bucket policies](https://docs.ceph.com/en/tentacle/radosgw/bucketpolicy/)

**Account users/groups/roles/policies/keys:** use the IAM-compatible action API at the RGW endpoint, with an account root or delegated principal. Supported actions are a subset; do not assume arbitrary managed-policy CRUD from AWS parity. Account creation/bootstrap belongs to Admin Ops/administrators, not ordinary IAM user creation. [Squid IAM action table](https://docs.ceph.com/en/squid/radosgw/iam/)

**Temporary credentials:** AssumeRole and AssumeRoleWithWebIdentity are documented RGW STS operations subject to gateway/identity-provider setup. User-supplied temporary credentials can be consumed independently of implementing STS issuance or organizational login. [Ceph STS](https://docs.ceph.com/en/tentacle/radosgw/STS/)

**Ownership changes:** distinguish linking the bucket to a user from rewriting ownership/ACLs of all existing objects. The CLI documents `bucket chown` as updating object ACLs, with resumable processing. No equivalent safe complete migration has been demonstrated through the chosen HTTP interfaces. Full ownership migration is experimental/deferred; neither a link operation nor AWS ownership controls should be labeled as that feature. [radosgw-admin ownership](https://docs.ceph.com/en/tentacle/man/8/radosgw-admin/)

**Placement/storage classes:** using a configured class in S3 requests is conditional and feasible. Defining pools, placements, and zone mappings is a different cluster-administrative task. The placement guide describes CLI configuration; no complete supported RGW Admin Ops route was established in this research. Classify configuration as not feasible through the proposed initial interfaces, pending a separate contract. Do not infer AWS meanings from matching class names. [Ceph placement](https://docs.ceph.com/en/tentacle/radosgw/placement/)

**Realms/zonegroups/zones, multisite synchronization:** full visibility is not established through ordinary S3/IAM/Admin Ops documentation. However, the separately hosted **Ceph Dashboard REST API** documents realm and multisite sync-status endpoints and JWT authentication. Thus HTTP-only visibility is conditionally feasible through a future fourth adapter, not inherently impossible without SSH. The API warns of compatibility changes. Keep it in Phase 4 with separate credentials, explicit deployment requirements, read-only scope, and versioned tests. Internal RGW replication endpoints are not a stable client contract by default. [Ceph Dashboard API](https://docs.ceph.com/en/tentacle/mgr/ceph_api/), [multisite operator guide](https://docs.ceph.com/en/tentacle/radosgw/multisite/)

**Diagnostics:** endpoint connectivity, TLS, safe request IDs, documented bucket statistics and permitted usage are feasible. Cluster health, daemon management, repair/resharding and complete replication health require separate supported APIs/tools and are outside the proposed RGW module. A fresh-looking sync response does not prove every object has replicated.

No SSH execution, shelling out to `radosgw-admin`, Cephx key handling, or raw cluster metadata mutation is part of the product baseline.

## Release qualification matrix

| Candidate | Why investigate | Present status |
| --- | --- | --- |
| Identified Squid (19.x) build/configuration | Primary initial S3 validation family | Scoped live .NET SDK reads PASSED on one user-identified 19.2.7 target; fixture mutations/cleanup separately confirmed. Exact package/build provenance and broader configuration remain pending; no family-wide claim |
| A pre-account-IAM release if still relevant to users | Legacy behavior and roles/STS boundary | Candidate only; support/maintenance status must be assessed when selected |
| Squid account-IAM configurations | Later account-IAM qualification on the selected family | Documentation researched only; identity configuration/access unknown; Phase 3, not MVP |
| Tentacle-family build | Newer versioned API reference | Documentation researched; no environment or tests |
| AWS S3 general-purpose buckets | Public v1.0 baseline | Dedicated test account/buckets not established |
| Other S3-compatible implementation | Detect accidental AWS/Ceph assumptions | No advertised support until full relevant suite passes |

Squid is the initial target, while additional qualification targets require explicit selection. Record exact version, packaging/distro backports, configuration, LAN/WAN/VPN path, TLS/CA trust, addressing mode, identity model, SDK version and test revision. Proxy use and TLS interception remain unknown; record them only when confirmed. Do not transfer Tentacle documentation assumptions to Squid without matching contracts and tests. Prefer two relevant Ceph releases for Phase 3, including both legacy and account-IAM paths when supported users need them. Add one test row per operation, authority, result and known limitation. A passing identified Squid configuration does not justify a family-wide compatibility label.

Key compatibility tests: SigV4 signed payload/trailer modes; checksums with multipart and encryption; direct bucket/prefix restrictions; dots in virtual-hosted bucket names; 403 versus unsupported; metadata/user pagination; tenant identifiers; account ownership/quota scope; key visibility and rotation; quota/rate enforcement lag; policy verbs/conditions; cancellation/abort; and old/new API schemas. Never probe capabilities through writes without a disposable test target and explicit test authority.

## Executed Session 5 qualification matrix

| Evidence | Result | Limit |
| --- | --- | --- |
| Offline SDK/TLS/signature/browser fixtures | PASS; preserved 168 tests | Controlled responses, not provider parity |
| HTTPS path-style direct listing and account discovery | PASS on selected RGW identity | Restricted-denial and virtual-hosted cases not tested live |
| Multi-page listing and exact-key metadata | PASS using isolated disposable fixtures | Leading slash, NFC/NFD, plus/percent and marker HEAD cases; not all keys/configurations |
| Fixture mutations/cleanup | 1 bucket + 1,008 PUTs confirmed; owned objects/bucket cleanup confirmed | Separate Python fixture tooling, not .NET application transfer support |
| Fedora/Openbox native GUI | User-reported smoke PASS | Framework-dependent; full native/vault/performance checks pending |
| Linux self-contained artifact | Locked publish PASS; headless launch reached BB-STARTUP | Successful native GUI without installed runtime remains pending |
| AWS S3 / other Ceph deployments | NOT TESTED | Never inferred from this target |

No administration, IAM, Dashboard or recovery compatibility is established. The laboratory infrastructure remains available for later development.
