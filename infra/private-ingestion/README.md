# One-shot private synthetic knowledge ingestion

This optional deployment writes knowledge through the project's Blob account,
which has public network access disabled. It does not weaken the Storage firewall
or the web runtime identity. An organisation policy can modify Storage public
network access. Do not re-enable public access, add public IP exceptions, use
account keys or SAS, or give the web runtime Blob roles to work around it.

## Scope and topology

`main.bicep` creates a dedicated VNet, a Blob-only private endpoint with a static
private IP, a linked `privatelink.blob` private DNS zone, an internal Container
Apps environment, a separate user-assigned identity and one **manual** job. It
never starts or schedules the job, changes the web app or the Storage account,
or deletes resources. It adds no VM, NAT gateway, crawler or public endpoint.

| Role of the separate identity | Scope |
| --- | --- |
| AcrPull | Container registry |
| Cognitive Services OpenAI User | Azure OpenAI account |
| Search Service Contributor, Search Index Data Contributor | Search service |
| Storage Blob Data Contributor | `documents` container only |

One execution has parallelism 1, **no automatic retries** and an 1800-second
deadline. The image is based on the .NET 10 SDK image (with PowerShell 7.6) and
runs as an unprivileged user. It gets tokens from the local managed-identity
endpoint and never prints them. The job runs `Private-Ingestion.ps1` (reusing
`KnowledgeSync.psm1`). Before any write, it checks that the Blob hostname
resolves **only to the configured private IP**.

## Approved input

The image contains only `approved-synthetic.md`, an original, visibly fictional
example. The job never mounts or scans directories, downloads documents, or
accepts text or paths from environment values. It writes one document
(`documentId: private-ingestion-synthetic`) whose only ACL entry is the supplied
user object ID. Do not substitute company data without a separate review.

## Build and validate offline

```powershell
docker build -f .\infra\private-ingestion\Dockerfile -t voice-private-ingestion:approved .
docker run --rm --network none `
  -e INGEST_ALLOWED_PRINCIPAL_ID=11111111-1111-4111-8111-111111111111 `
  -e INGEST_DOCUMENT_UPDATED_AT=2026-09-20T00:00:00Z `
  -e INGEST_INDEX_NAME=meeting-knowledge `
  voice-private-ingestion:approved -ValidateOnly
```

The expected result is `PASS`, `scope: offline-synthetic-plan`, `uploaded: false`.
`scripts\infra\tests\Test-Offline.ps1` runs the template and script checks. Push
the image and use its **registry digest**, not the local image ID.

## Deploy without running ingestion

1. Copy `parameters.example.json` to a git-ignored `parameters.local.json`. Fill in the existing resource names, the image digest, the user object ID and an RFC3339 document version.
2. Check that the default address ranges (`10.246.0.0/23`, endpoint IP `10.246.1.4`) do not overlap other networks.
3. Keep `createIndex: false` unless the index does not exist yet. An existing index is never replaced.
4. Keep `confirmExclusiveMaintenance: false` until every application reader is stopped. The parameter records that state; it does not stop anything.
5. Review the cost of the private endpoint, the private DNS zone and the job run against current [Azure pricing](https://azure.microsoft.com/pricing/).
6. Compile with Bicep. Then run `deployment group validate` and `what-if` before `create`, through the existing Az adapter (`Import-Module .\scripts\infra\Common.psm1`, `Set-AzureCli -AuthProvider AzPowerShell`, `Invoke-AzJson`). `Deploy.ps1` is not used here because it accepts only the main template.
7. No deployment step starts ingestion. Before a run, check that the endpoint connection is approved, the DNS link exists and the role assignments have propagated.

`enableDiagnostics` (default false) adds a dedicated Log Analytics workspace
(30-day retention, Entra-only access) for this job's console and system logs
only. Use it only with this synthetic image.

## Run once, verify and disarm

1. Stop all application readers. Start **one** execution of the job (`POST .../jobs/<job>/start`).
2. Poll that execution until it is **Succeeded** or **Failed**. Wait a little longer than the 30-minute deadline. Do not start another execution because a client-side wait expired.
3. After success, query Search for `documentId eq 'private-ingestion-synthetic'` with the approved principal's ACL filter. A different principal must get zero results.
4. Disarm: set `confirmExclusiveMaintenance: false` and `createIndex: false`, apply the deployment again, and read back `INGEST_CONFIRM_EXCLUSIVE_MAINTENANCE=false` and `INGEST_CREATE_INDEX=false`. A later accidental start then fails at the maintenance guard.
5. Restart the app and check its health endpoints.

If the run fails, keep readers stopped, inspect the redacted phase and status, and
retry explicitly with the same document version (with `createIndex: false` if the
failed run created the index). Recovery never adds network exceptions, identity
roles, schema replacement or data deletion.

References: [jobs schema](https://learn.microsoft.com/azure/templates/microsoft.app/2024-03-01/jobs),
[managed identity REST endpoint](https://learn.microsoft.com/azure/container-apps/managed-identity),
[Storage private endpoints](https://learn.microsoft.com/azure/storage/common/storage-private-endpoints).
