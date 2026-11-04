# Release script

`Release.ps1` builds the reviewed commit in Azure Container Registry, pins the image digest and deploys it to the
staging Container App. It then checks health and security headers and runs an optional smoke test. With
`-Production`, it promotes the same digest to production and prints the rollback commands.

It is a **dry run by default**: it checks the source and prints the plan and the commands, but runs no Azure command.
Add `-Apply` to run them. It runs on Windows PowerShell 5.1 and PowerShell 7. The operator procedure and the rollback
commands are in the [release and rollback runbook](../../docs/runbooks/release-and-rollback.md).

## Prerequisites

- Azure CLI signed in to the approved tenant. The script passes `--subscription` on every call and never runs
  `az account set`.
- `git` and `tar` on `PATH`, or `-GitPath` and `-TarPath`. `-AzPath` and `-AzPrefix` select a portable Azure CLI.
- A git-ignored `infra/release.local.json`, copied from [`infra/release.example.json`](../../infra/release.example.json):

| Field | Meaning |
| --- | --- |
| `version` | `1` |
| `subscriptionId`, `tenantId` | The approved subscription and its tenant; the CLI account must match both |
| `projectId` | Ownership tag GUID. The shared target loader requires it; only `scripts/infra` uses its value |
| `resourceGroup` | Resource group of the production app |
| `stagingResourceGroup` | Optional; defaults to `resourceGroup` |
| `registryName` | Existing registry name without `.azurecr.io` |
| `imageRepository` | Image repository, `voice-assistant` |
| `stagingApp`, `productionApp` | Container App names; they must differ |

Parameters such as `-SubscriptionId`, `-ResourceGroup`, `-RegistryName`, `-StagingApp` and `-ProductionApp` override
single fields, or describe the whole target when there is no file. They never replace the configured subscription or
tenant. Errors name invalid fields, never their values. `-ConfigPath` or `VOICE_ASSISTANT_RELEASE_CONFIG` selects
another file.

`-Prefix` (default `rel`, 1–16 lowercase letters or digits) names the image tag and the revision suffix:
`<prefix>-<first 7 characters of the commit>`. Revision suffixes are single-use, so use a new prefix to redeploy the
same commit after a failed attempt. `-ImageDigest` promotes a digest that an earlier run built and staged.

## What `-Apply` does

1. **Source.** The working tree must be clean and `HEAD` must equal `-Ref` (default `origin/main`). The script does
   not fetch.
2. **Account.** `az account show` must return the configured subscription and tenant. Both app names are read before
   the build, so a typo fails early.
3. **Build.** `git archive` exports the commit to a new folder under `-WorkRoot` (default `C:\t`; a short path avoids
   Windows long-path failures). The script queues `az acr build --no-wait`, polls the run until `Succeeded`, and reads
   the digest from its output images. It does not trust the CLI exit code.
4. **Staging.** `az containerapp update` deploys `<registry>.azurecr.io/<repo>@<digest>`. The script waits until the
   revision is active and is the latest ready revision, and until `/health/ready` returns 200 with the expected
   `Strict-Transport-Security`, `X-Content-Type-Options`, `Referrer-Policy`, `Content-Security-Policy`,
   `Permissions-Policy` and `Cache-Control` headers.
5. **Smoke test** (optional, `-SmokeTest`). A scriptblock or `.ps1` path, called with an object that has
   `Environment`, `BaseUrl`, `App`, `Revision`, `Image`, `Digest` and `Commit`. A thrown error or a nonzero
   `$LASTEXITCODE` stops the release before production. The
   [quality baseline](../../docs/runbooks/quality-baseline.md#smoke-test-in-a-release) has a synthetic example.
6. **Production** (only with `-Production`). The script prints the current revision, its image and the rollback
   commands, deploys the same digest, repeats the checks, and prints the rollback commands again.

Timeouts: `-BuildTimeoutMinutes` (45), `-ReadyTimeoutSeconds` (600), `-PollSeconds` (15).

## Troubleshooting

| Symptom | Action |
| --- | --- |
| `Release target is incomplete or invalid: ...` | Fill the named fields in `infra/release.local.json` |
| `The working tree is not clean` / `HEAD is not origin/main` | Commit or stash; `git fetch origin`; `git switch --detach origin/main` |
| `Build run <id> ended with status Failed` | Read the output of the printed `az acr task logs ...` command |
| `Could not identify the build run` | `az acr task list-runs --registry <acr> --subscription <id> --top 5`; rerun with a new `-Prefix` |
| `... already has revision ...` | Choose a new `-Prefix` |
| Health or header check fails | Inspect the revision and its logs; in production, use the printed rollback |

## Tests

`tests/Test-Release.ps1` runs offline with in-process fakes for `az`, `git`, `tar`, `Invoke-WebRequest` and
`Start-Sleep`, and the synthetic fixture `scripts/infra/tests/fixtures/release.fixture.json`. `scripts/verify-release.ps1`
runs it as the `release-script-offline-tests` stage.

```powershell
pwsh -NoProfile -File .\scripts\release\tests\Test-Release.ps1
```
