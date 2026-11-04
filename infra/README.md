# Azure infrastructure and knowledge ingestion

`main.bicep` deploys the browser app. One Azure Container App serves the built web client from the API's `wwwroot`;
API and WebSocket traffic use the same HTTPS origin on container port 8080. No CDN, Teams bot, Graph connector,
document crawler or fake-authentication mode is provisioned. The image repository (`voice-assistant`) and the default
ARM deployment name keep the earlier project name.

## What is provisioned

`main.bicep` targets an existing resource group. It creates a Consumption Container Apps environment and app with a
user-assigned runtime identity, a Speech account with a custom subdomain, an Azure OpenAI account with chat and
embedding deployments (plus an optional live-answer deployment, off by default), Azure AI Search, a Storage account
with a private Blob `documents` container and a zero-replica alert. With `enableDiagnostics=true` (default) it also
creates Log Analytics and Application Insights.

The image is a required, already-pushed digest in an existing Azure Container Registry (ACR) in the same
subscription. The registry must use ordinary RBAC, not ABAC repository permissions. The deploying identity needs
deployment and role-assignment permissions in the resource group and on the registry. Providers must be registered.

| Identity | Role | Scope |
| --- | --- | --- |
| Runtime managed identity | Cognitive Services Speech User | Speech account |
| Runtime managed identity | Cognitive Services OpenAI User | OpenAI account |
| Runtime managed identity | Search Index Data Contributor (reads for grounding; writes only each user's own uploaded materials) | Search service |
| Runtime managed identity | AcrPull | Existing registry |
| Optional ingestion operator | Search Service Contributor and Search Index Data Contributor | Search service |
| Optional ingestion operator | Cognitive Services OpenAI User | OpenAI account |
| Optional ingestion operator | Storage Blob Data Contributor | `documents` container only |

The API has no Blob access. To create the operator roles, set `ingestionPrincipalId` to the operator's Entra
**object ID** (not a client ID); its type defaults to `User`. Only `Deploy.ps1 -Apply` creates role assignments.
The ingestion preflight also needs read access to the three services, for example Reader on the resource group.

Speech, Azure OpenAI and Search accept Entra authentication only; keys and local authentication are disabled. Search
omits `authOptions`, because Azure rejects them when local authentication is disabled. Storage sets
`publicNetworkAccess: Disabled`, with no override. To load documents into the deployed baseline, use the
[private ingestion route](private-ingestion/README.md). Local ingestion needs an approved private network path; an
Entra sign-in does not bypass the storage network boundary. Source blobs are operator-only archives.

## Prerequisites and deployment

### Local release target (`infra/release.local.json`)

Environment identifiers stay out of Git. Copy `infra/release.example.json` to `infra/release.local.json` (ignored
through `*.local.json`) and fill in:

| Field | Used by | Meaning |
| --- | --- | --- |
| `version` | all | Always `1` |
| `subscriptionId`, `tenantId` | every online script | The only subscription and tenant the scripts may touch |
| `projectId` | every online script | Ownership-tag GUID; generate one with `[guid]::NewGuid()` for a new environment |
| `resourceGroup`, `registryName`, `imageRepository` | `scripts/release/Release.ps1` | Production resource group, existing ACR name (without `.azurecr.io`), image repository (`voice-assistant`) |
| `productionApp`, `stagingApp` | `scripts/release/Release.ps1` | Container App names; add `stagingResourceGroup` when staging is in another group |

The online modes of `Deploy.ps1`, `Import-Knowledge.ps1` and `Update-SemanticIndex.ps1` read the subscription and
tenant only from this file. A missing file, a placeholder or a different `-SubscriptionId`/`-TenantId` stops them
before any Azure request; errors name the field, never its value. `VOICE_ASSISTANT_RELEASE_CONFIG` selects another
path. Offline modes (the default) do not need the file.

### Main application prerequisites

1. Sign in with Azure CLI. The scripts pass `--subscription` explicitly and never run `az account set`. To use an
   existing Az PowerShell (Az.Accounts) sign-in instead, add `-AuthProvider AzPowerShell` to `Deploy.ps1`,
   `Import-Knowledge.ps1` or `Update-SemanticIndex.ps1`.
2. Create two single-tenant Entra app registrations: an API and a public SPA. The API issues **v2 access tokens** and
   exposes the delegated scope `Meeting.Access`; grant the SPA that permission and complete consent. The SPA uses
   authorization code with PKCE, no client secret and no implicit flow. Its redirect URI is the browser HTTPS origin.
3. Build the API image from the repository root and push it to the ACR:
   `docker build -f .\src\VoiceAssistant.Api\Dockerfile -t <registry>.azurecr.io/voice-assistant:<version> .`
   The Dockerfile also builds the web client into `wwwroot`. Pass the image to the template by digest (`@sha256:`).
   For later releases, `scripts/release/Release.ps1` builds in ACR, checks staging and promotes the same digest
   ([release script](../scripts/release/README.md), [runbook](../docs/runbooks/release-and-rollback.md)).
4. Download an official [Bicep CLI release](https://github.com/Azure/bicep/releases) and verify its SHA256 (CI pins
   the version and checksum in `.github/workflows/ci.yml`). The scripts run on PowerShell 7 or Windows PowerShell 5.1.
5. Copy `parameters.example.json` to `parameters.local.json` (ignored by Git) and replace every `REPLACE_` value.
   Check quota, policy, data residency and regional availability before you choose models, SKUs and regions. The
   embedding model is text-embedding-3-small with 1536 dimensions; another dimension needs an index migration.

`tenantId`, `apiAudience` (the API client ID), `spaClientId` and `apiScope` (the full `api://.../Meeting.Access` URI)
are secure ARM parameters but public identity metadata, not secrets. The browser reads them from
`GET /api/client-config` ([websocket-v1](../contracts/websocket-v1.md)).

```powershell
$bicep = 'C:\tools\bicep.exe'; $parameters = '.\infra\parameters.local.json'; $group = '<existing-resource-group>'
.\scripts\infra\Deploy.ps1 -ParametersFile $parameters -BicepPath $bicep     # offline checks (the default)
.\scripts\infra\Deploy.ps1 -ParametersFile $parameters -BicepPath $bicep -ResourceGroup $group -Validate
.\scripts\infra\Deploy.ps1 -ParametersFile $parameters -BicepPath $bicep -ResourceGroup $group -WhatIf
# Only after the cost, the role grants and the change are approved:
.\scripts\infra\Deploy.ps1 -ParametersFile $parameters -BicepPath $bicep -ResourceGroup $group -Apply
```

What-if prints only resource IDs and change types. `-Apply` repeats validation first. Validation cannot prove quota,
image pull, consent or data-plane access. The template sets the default-domain browser origin as the API's only
allowed origin; after deployment, add it as the SPA redirect URI. Custom domains are not configured.

### Optional demo login (`POST /api/demo/login`)

For demos without Entra accounts, one shared username and password can issue bearer tokens for one fixed identity.
Entra tokens stay accepted. `main.bicep` does not set these values. Configure them on the Container App, with the
password and signing key as Container Apps secrets (`secretref:`), never in Git:

| Setting | Meaning |
| --- | --- |
| `DemoLogin__Username` | Enables the endpoint when non-empty |
| `DemoLogin__Password` | Shared password (secret) |
| `DemoLogin__ObjectId` | GUID used as the token's `oid`; uploaded materials belong to it |
| `DemoLogin__SigningKey` | Base64 HMAC key of at least 32 bytes (secret) |
| `DemoLogin__TokenDays` | Token lifetime in days: default 7, allowed 1 to 30; any other value prevents startup |

The endpoint takes `{"username":"...","password":"..."}` and returns `{token, username, expiresAt}`. Outside Fake
mode the request needs an `Origin` header equal to the app's origin; scripts must send it too. Errors: 400 malformed
body, 401 wrong credentials, 403 missing or other `Origin`, 413 body over 1 KB, and 429 with `Retry-After: 600` after
10 failures from one address in 10 minutes. The token is an HS256 JWT (issuer and audience `voice-assistant-demo`,
scope `Meeting.Access`). It cannot be revoked; rotate the signing key to invalidate all tokens. Anyone with the
password acts as that identity, so use a dedicated synthetic object ID. `Deploy.ps1 -Apply` replaces the container
environment list, so set these values again afterwards. QualityEval signs in through this endpoint
([quality baseline](../docs/runbooks/quality-baseline.md)).

### One replica is intentional, not high availability

The API keeps single-use, 30-second WebSocket tickets in memory, so the template sets `minReplicas = maxReplicas = 1`
with one active revision. A restart or a new revision loses tickets and active sessions. Drain users before updates.
Do not scale out until tickets move to shared storage. Search also starts with one replica and one partition.

## Approved knowledge import (`Import-Knowledge.ps1`)

The default run is offline: no token, request, embedding or upload. The manifest lists UTF-8 `.txt` and `.md` files
inside `-ContentRoot` (at most 1 MiB per file, 100 operations per manifest). Path traversal, symbolic links, empty
text and public, wildcard or empty ACLs fail. `-Apply` needs an approved private network path to the Blob account.

`scripts\infra\tests\fixtures\manifest.json` shows the shape. Each upsert has `documentId`, `operation: "upsert"`, a
relative `path`, `title`, the original HTTPS `url`, an RFC 3339 `updatedAt` and `allowedPrincipalIds`. The IDs must
be **user object IDs**: the API filters on the caller's `oid` and does not resolve groups. The operator confirms
export approval and current document permissions; the script cannot verify them.

```powershell
.\scripts\infra\Import-Knowledge.ps1 -ManifestPath <approved>\manifest.json -ContentRoot <approved>
# Only in an approved maintenance window, with the app stopped or drained:
.\scripts\infra\Import-Knowledge.ps1 -ManifestPath <approved>\manifest.json -ContentRoot <approved> `
  -ResourceGroup <existing-resource-group> -SearchName <search-name> -OpenAIName <openai-name> `
  -StorageAccountName <storage-name> -CreateIndex -ConfirmExclusiveMaintenance -Apply
```

- `-CreateIndex` is for the first run only; it never replaces an existing index. An existing index must match
  `infra/knowledge/index.json`, which adds `documentId` and `sourceHash` to the API fields.
- Chunks are at most 1,500 UTF-16 code units with 150 overlap; the API's upload path uses its own chunker
  ([knowledge-v1](../contracts/knowledge-v1.md)). The normalized text is stored in
  `documents/<index>/<documentId>.txt`.
- The script uses the operator's Entra tokens in memory and never prints keys, tokens or request bodies.

The script owns the index exclusively. Before an update, especially a permission revocation, stop or drain the app:
active sessions may hold retrieved evidence. `-ConfirmExclusiveMaintenance` is an acknowledgment; it stops nothing.
For changed content, title, URL or ACL, increase `updatedAt`. Old chunks are removed before new ones are published.
To delete a document, submit `{ "documentId": "<id>", "operation": "delete", "updatedAt": "<later time>" }`; leaving
it out of a manifest does not delete it. A per-index Blob version ledger stops old manifests from restoring deleted
sources; do not delete it. A 60-second Blob lease serializes writers. There is no cross-service transaction: after a
failed run, keep the app stopped, retry the same manifest without `-CreateIndex`, and check ACL isolation with an
authorized and an unauthorized test identity. Search writes are eventually consistent, so revocation is not instant.

## Operations

### Semantic ranking and existing-index migration

The API reranks hybrid results with the semantic configuration `meeting-semantic` and drops results below a minimum
reranker score; ACL filtering always applies first. The threshold values are recorded only in the
[quality baseline](../docs/runbooks/quality-baseline.md). The template also turns on the four
[experiment switches](../src/VoiceAssistant.Api/README.md#experiment-switches) that are off in the API by default.
`searchSemanticSearch` takes `free` or `standard` (default). When the free monthly quota runs out, every grounded
reply fails with HTTP 402 ([runbook](../docs/runbooks/search-semantic-quota.md)). The API never falls back to raw
vector results.

To add the semantic configuration to a populated index, do not delete or reindex it. Enable the capability through
the template, then run:

```powershell
.\scripts\infra\Update-SemanticIndex.ps1 -SchemaFile <approved>\search-index-metadata.json   # offline review
.\scripts\infra\Update-SemanticIndex.ps1 -Apply -ResourceGroup <existing-resource-group> -SearchName <search-name>
```

`-Apply` reads fresh metadata (index `meeting-knowledge` by default), checks compatibility and adds only the missing
configuration with `If-Match` on the exact ETag, so other settings stay unchanged. A same-name conflict is rejected;
correct metadata is a no-op; HTTP 412 is not retried. A final GET must confirm the result. The script never reads or
writes index documents. The operator needs Search Service Contributor. Rebuild the private-ingestion image before its
next run.

### Diagnostics, health and cost

- **Logs and metrics.** With `enableDiagnostics=true`, console and system logs go to a 30-day, Entra-only Log
  Analytics workspace (`${namePrefix}-logs`). Application Insights (`${namePrefix}-insights`) receives only the
  API's content-free metrics through the runtime identity's Monitoring Metrics Publisher role: no request traces, no
  logs and no Statsbeat. Instrument names:
  [content-free timing metrics](../src/VoiceAssistant.Api/README.md#content-free-timing-metrics).
- **No content in logs.** The API logs no transcript, answer, query or request URL. Do not enable URL logging at
  ingress or proxies: WebSocket upgrade URLs carry one-use tickets.
- **Alert.** The alert fires on `Replicas < 1`. Set `alertActionGroupId` to route notifications.
- **Health.** Startup and liveness use `/health/live`; readiness uses `/health/ready`, so a revision gets traffic only
  when ready. Warm-up stages are in the [API README](../src/VoiceAssistant.Api/README.md). Warm-up makes no billable
  call, so verify RBAC and data-plane access separately with synthetic data.
- **Cost.** Drivers are the always-on app (2 vCPU and 4 GiB by default, `containerCpu`), Search capacity, Speech
  time, tokens, embeddings, storage and the registry. Set budget alerts before `-Apply`; **a budget alert is not a
  spending cap.**

### Workbook

With `enableDiagnostics=true` and `deployWorkbook=true` (both default), the template also deploys the Azure Monitor
workbook "Live Coach telemetry". It charts reply latency, reply outcomes and decisions, token use and reliability
counters from the content-free metrics, plus per-reply first-line percentiles from the `ReplySummary` console lines.
To add it to an existing deployment without touching the Container App, deploy `workbook.bicep` alone:

```powershell
az deployment group create --resource-group <group> --template-file .\infra\workbook.bicep `
  --parameters appInsightsId=<application-insights-id> logAnalyticsWorkspaceId=<log-analytics-workspace-id>
```

Histogram tiles show count-weighted percentiles of per-minute means, because the exporter sends no buckets; the log
tiles give exact per-reply percentiles.

## Offline tests

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\infra\tests\Test-Offline.ps1 `
  -BicepPath C:\tools\bicep.exe -BackendSchemaPath .\contracts\search-index.json
```

The tests need no Azure account. They check the compiled template, the subscription lock, ACLs, chunking, mocked
uploads, deletions, leases and recovery, plus the Azure process adapter, private ingestion and semantic index.
`-BackendSchemaPath` compares the index with the API's schema. CI runs them through `scripts/verify-release.ps1`.
