# Explicitly opted-in read-only S3 qualification

`BucketBus.Integration.slnx` contains the separate `BucketBus.S3.IntegrationTests` project. Ordinary solution tests do not include it. To validate its offline guards without any live case, even when live environment variables happen to be set:

```bash
./eng/validate-integration.sh
```

Running the integration solution with opt-in absent skips its live case before configuration, credential resolution, client construction or network access. An opted-in run with invalid configuration fails with a fixed message; it never silently falls back to another credential source.

Create a local JSON file under ignored `docs/internal/`. All values below are fictional examples; select the specifically authorized target and named literal shared-credentials section. The configuration and evidence paths must be absolute, within this checkout's `docs/internal/`, without symlinks.

```json
{
  "Schema": 1,
  "Endpoint": "https://storage.example.test",
  "ApprovedEndpoint": "https://storage.example.test",
  "Region": "us-east-1",
  "Addressing": "Path",
  "Bucket": "explicit-approved-bucket",
  "CredentialProfile": "explicit-selected-profile",
  "DiscoverBuckets": false,
  "MaximumPages": 3,
  "HeadKeys": ["existing-object"],
  "EvidenceFile": "/absolute/checkout/docs/internal/live-results.json"
}
```

The harness reads only the named section in `~/.aws/credentials`, supporting literal access key/secret and optional session token. Process, role, SSO and other profile providers are rejected; no environment/default-profile/container/metadata resolution is attempted. Credential values are never command arguments or output. No credential files are written. Account discovery is separately selected; direct bucket listing runs first.

Supply `BUCKETBUS_APPROVED_ENDPOINT` independently from the operator's approval,
never by copying/reading either endpoint field out of the target JSON. Missing,
unsafe or mismatched approval fails before credential resolution/client creation.
Both approval and target must be HTTPS authorities; canonical host/default-port
forms are compared. The JSON `ApprovedEndpoint` field remains only a consistency
check, not authorization. Changing both JSON fields cannot override the external
approval. This guards configuration mistakes, not a malicious operator who can
change both the process environment and the file.

After selecting the separately approved authority and explicit local file
(the endpoint below is fictional, not a usable laboratory authorization):

```bash
BUCKETBUS_RUN_LIVE_TESTS=1 \
BUCKETBUS_APPROVED_ENDPOINT='https://storage.example.test' \
BUCKETBUS_LIVE_CONFIG="$(pwd)/docs/internal/live.json" \
  dotnet test BucketBus.Integration.slnx -c Release --no-build --no-restore --filter Category=LiveS3
```

HTTPS validation is normal; redirects are disabled. The harness bypasses environment proxies to reach the explicitly selected target directly. It uses the production adapter with GET/HEAD only, at most three physical sends / 30 seconds per operation, at most eight pages and twenty configured metadata keys, and a six-minute suite cancellation budget. It never downloads object bodies or mutates resources. A result file records fixed status categories without object keys, credentials, response bodies or raw exceptions. Missing optional coverage is explicitly reported; a successful test case does not mean every qualification scenario passed.

Session 5 separately used authorized disposable write tooling to create pagination/exact-key fixtures, followed by this unchanged read-only suite and manifest-based cleanup. That tooling and its environment-specific manifest remain private. It is not an application transfer implementation or an automatic setup/cleanup hook in the read-only harness. Future write experiments need their own explicit opt-in, exact target, ownership manifest, bounded operations and recorded uncertain/cleanup outcomes. Laboratory authority does not authorize cluster, network or VM teardown.
