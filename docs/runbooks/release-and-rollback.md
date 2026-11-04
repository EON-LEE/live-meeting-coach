# Release and rollback

This is the operator procedure for shipping a new image of the Live Coach Container App. The tool is
[`scripts/release/Release.ps1`](../../scripts/release/README.md). Every command passes `--subscription`. Never run
`az account set`.

## Before you start

- CI (**Build and test**) is green for the commit you release.
- `infra/release.local.json` exists: a git-ignored copy of `infra/release.example.json` with your target.
- You are signed in with `az login` to the approved tenant, and `az account show --subscription <id>` works.
- No meeting is running. The app has one replica and in-memory WebSocket tickets, so a revision switch ends live
  sessions. Users reconnect with Start.
- You have a new revision prefix (for example `w2`). Revision suffixes are single-use.

## Release

1. Check out exactly what you release: `git fetch origin`, then `git switch --detach origin/main`.
2. Run a dry run and read the plan. No Azure command runs: `.\scripts\release\Release.ps1 -Prefix w2`.
3. Build and deploy to **staging**, with your smoke test if you have one:
   `.\scripts\release\Release.ps1 -Prefix w2 -Apply -SmokeTest C:\ops\smoke.ps1`.
   The run prints the pinned image (`<registry>.azurecr.io/voice-assistant@sha256:...`). Keep that digest.
4. Optional: check staging by hand. Open `/live.html`, run the audio demo, and ask one question with document search
   on. For a measured gate, run the [quality baseline](quality-baseline.md) comparison against staging.
5. Promote the **same digest** to production:
   `.\scripts\release\Release.ps1 -Prefix w2 -Apply -Production -ImageDigest sha256:<digest from step 3>`.
   Before it changes production, the script prints the current revision, its image and the rollback commands. Copy
   them into your release notes outside this repository.
6. For about 15 minutes after the release, check that `/health/ready` returns 200, the Live page connects, and the
   console logs show no new `Meeting provider failed` or `Meeting retrieval unavailable` warnings.

Steps 3 and 5 can be one run (`-Apply -Production`). Promoting with `-ImageDigest` proves that staging and production
run the same image.

## Roll back

Roll back when production health or headers fail, errors rise, or replies regress. Use the commands that the script
printed; they contain the names.

**A. Redeploy the previous image (preferred).** This creates a new revision with the old digest. Traffic switches only
when it is ready.

```powershell
az containerapp update --name <app> --resource-group <group> --subscription <id> `
  --image <previous image> --revision-suffix rb-<previous suffix>
```

**B. Reactivate the retained revision.** Single-revision mode deactivates the previous revision but keeps it, so it
can be activated again. Traffic weights apply only in multiple-revision mode.

```powershell
az containerapp revision activate --name <app> --resource-group <group> --subscription <id> --revision <previous revision>
az containerapp ingress traffic set --name <app> --resource-group <group> --subscription <id> --revision-weight <previous revision>=100
```

If the printed commands are lost, list the revisions with their images:

```powershell
az containerapp revision list --name <app> --resource-group <group> --subscription <id> --all `
  --query "[].{name:name, active:properties.active, created:properties.createdTime, image:properties.template.containers[0].image}" -o table
```

After a rollback, check that `/health/ready` returns 200 and the Live page works. Keep the bad digest for analysis
and fix forward with a new prefix.

## Notes

- `Release.ps1` changes only the image and revision suffix. `scripts/infra/Deploy.ps1 -Apply` redeploys the whole
  template and replaces the container's environment variables; re-apply manual settings such as `DemoLogin__*`.
- In Azure mode, `/health/ready` waits for the credential, Azure OpenAI and Speech warm-up (Search is best-effort).
  It does not prove grounded answers; use a smoke test or the quality baseline for that.
- Script errors and fixes are in [Troubleshooting](../../scripts/release/README.md#troubleshooting).
