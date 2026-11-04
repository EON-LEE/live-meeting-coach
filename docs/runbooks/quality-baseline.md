# Quality baseline: QualityEval against staging

QualityEval speaks a fictional company's questions with Azure AI Speech, streams them through the WebSocket protocol
of a deployed Live Coach, and scores the replies and stage latency. A baseline run plus `compare` is the release gate.
Commands and options are in the [QualityEval README](../../tools/QualityEval/README.md).

## Latency targets

Measure from the actual end of the speaker's utterance, not from the final transcription event. These are initial
targets, not measured guarantees:

| Metric | p50 target | p95 target |
| --- | --- | --- |
| General conversation: first useful complete sentence | 1 s | 2.5 s |

Grounded or private-fact questions are a separate workload. Do not mix them with the no-retrieval path to get a
better percentile. Report the region, retrieval mode, model deployment, sample size, cold or warm start,
cancellations and failures together with latency. Fake-provider results cannot establish these targets.

## Reference numbers (staging, October 2026)

108 synthetic runs (36 items × 3 repeats, three accents) against staging, scored after the scorer counted declined
commitments as abstaining:

| Metric | Value |
| --- | --- |
| `passRate` | 0.98 |
| `factRecall` | 0.98 |
| `abstainRate` (not-covered and commitment items) | 1.00 |
| `forbiddenRate` | 0 |
| `retrievalHitRate` (materials-required items) | 0.99 |

| Latency from speech end | p50 | p95 |
| --- | --- | --- |
| Final transcript (`speechEndToFinal`) | about 1.0 s | about 1.2 s |
| First line | about 2.43 s | about 2.84 s |
| Completed reply (`speechEndToCompleted`) | about 2.48 s | about 2.88 s |

**Reranker threshold.** This document is the single place for these numbers. `Azure__SearchMinimumRerankerScore`
accepts 0 to 4. The API default is 2.0; the deployment template (`infra/main.bicep`) sets 1.5, which the run above
used. At 2.0, the same items scored `passRate` 0.88 and `retrievalHitRate` 0.84. A reranker score does not prove
that a reply is supported, so calibrate again with representative authorized material. These rounded numbers show
the expected range; gate on `compare` against a full baseline file kept outside the repository.

## Rules

- **Staging only.** Never point QualityEval at production.
- **Dedicated synthetic identity.** Never use your real account: the run uploads the synthetic corpus to that
  identity's materials. Set `EVAL_FORBIDDEN_OID_SHA256` (the hash of your real object ID) and
  `EVAL_EXPECTED_OID_SHA256` (the hash of the evaluation identity), so a wrong sign-in stops before any upload.
- **Synthetic content only.** Results hold aggregates only. Write outputs to the git-ignored `tools/QualityEval/out/`
  or to a folder outside the repository. Never commit them.
- **Credentials only in environment variables** of the current shell, never in files.

## Procedure

1. Give the staging app a sign-in for the evaluation identity. Use the
   [demo login](../../infra/README.md#optional-demo-login-post-apidemologin) with a dedicated `DemoLogin__ObjectId`,
   or an Entra test account whose delegated `Meeting.Access` token you supply.
2. Set the environment variables listed in [Real run](../../tools/QualityEval/README.md#real-run-against-staging).
3. Run `validate`, then `synthesize --allow-tts` once (the audio is cached), then upload the corpus with
   `materials upload --allow-remote --allow-materials-upload`.
4. Measure the revision that is on staging now: `run --allow-remote --repeats 3 --write-details` (about 20–30
   minutes). `--write-details` keeps the synthetic replies, so the run can be rescored later.
5. Deploy the candidate to staging (`scripts/release/Release.ps1 -Apply`) and measure it the same way.
6. Run `compare BASELINE CANDIDATE`. Promote with `Release.ps1 -Production -ImageDigest ...` only when `compare`
   passes, or when every regression is understood and recorded.

Re-baseline after an intended behavior change, never to hide a regression.

## Gate thresholds

`compare` exits **0** (pass), **1** (regression) or **2** (incompatible runs). The rules are in
[Baseline and gate](../../tools/QualityEval/README.md#baseline-and-gate). For a manual run, use the defaults: a
latency p50 or p95 fails when it grows by more than 10% **and** more than 100 ms. The scheduled gate that runs
outside this repository uses looser latency thresholds: `--max-p95-regression 0.25 --max-p50-regression 0.25
--min-latency-delta-ms 300` (25% and 300 ms). Quality rules are the same in both cases. The optional LLM judge is
advisory unless `compare --gate-judge` is set.

When the scorer or `items.json` changes, `compare` exits 2. Rescore both runs first, as described in
[Rescore a finished run](../../tools/QualityEval/README.md#rescore-a-finished-run-offline).

## Smoke test in a release

A three-item, single-accent run takes about a minute. It fails when any session does not complete:

```powershell
$smoke = {
    param($c)
    python .\tools\QualityEval\quality_eval.py run --base-url $c.BaseUrl --allow-remote --repeats 1 --accents en-US `
        --items fact-security-contact,number-ga-date,multi-plan-fit --output-dir "C:\eval\smoke-$($c.Revision)"
    if ($LASTEXITCODE -ne 0) { throw "QualityEval smoke run failed ($LASTEXITCODE)." }
}
.\scripts\release\Release.ps1 -Prefix w2 -Apply -SmokeTest $smoke
```

## Cost

A full run streams about 15–20 minutes of audio to Speech, makes one reply call and one classifier call per session,
and sends a few hundred semantic-ranker queries. On the Search **free** semantic plan, a few runs use up the monthly
allowance (HTTP 402, see [search-semantic-quota.md](search-semantic-quota.md)). Text-to-speech is paid once per clip
and then cached. The judge adds about one call per session.
