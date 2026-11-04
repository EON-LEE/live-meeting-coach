# QualityEval: synthetic evaluation of the full Live Coach path

QualityEval measures what a meeting participant gets. It speaks a fictional company's questions with Azure AI Speech
text-to-speech in several accents and streams them in real time through the
[WebSocket protocol](../../contracts/websocket-v1.md) to a Live Coach server, which runs its normal pipeline. It scores
stage latency, recognized key terms, and the facts, forbidden claims, abstentions and readability of each reply. It
reports p50/p95 with question-level bootstrap 95% confidence intervals (CIs), and `compare` gates a candidate against
a baseline. The release procedure, targets and reference numbers are in the
[quality baseline runbook](../../docs/runbooks/quality-baseline.md). Everything in this folder is **synthetic**: the
company Kestrova Analytics, its people, products, prices and dates are invented.

| Path | Content |
| --- | --- |
| `corpus/corpus.json`, `corpus/docs/*.md` | 7 synthetic documents (product, pricing, timeline, team, security FAQ, support SLA, integrations) |
| `corpus/items.json` | 36 synthetic items in 7 categories: `fact`, `number`, `asr-term`, `multi-turn`, `not-covered`, `commitment`, `general` |
| `qualityeval/` | Python standard-library implementation (Python 3.10+; `azure-identity` optional) |
| `tests/` | Offline tests with an in-process fake server; no network, Azure or .NET |
| `out/`, `.tts-cache/` | Git-ignored outputs and cached TTS audio. Never commit them. |

## Privacy rules

- **Synthetic content only.** The loader refuses a corpus or item file not declared `"classification": "synthetic"`.
  The corpus must be marked `"fictional": true`, and every document must carry the manifest's synthetic marker.
- **Outputs stay out of Git.** Outputs go only to the git-ignored `out/` and `.tts-cache/` folders or outside the
  repository. Existing outputs are not overwritten.
- **Results hold aggregates only.** `results.json` holds numbers, synthetic item IDs and key terms; the server origin
  and object ID appear only as SHA-256 prefixes. `--write-details` adds synthetic transcripts and replies in
  `details.jsonl`, which rescoring needs.
- **Credentials come from environment variables only.** They are never written to disk or logs. Redirects are refused.
- **Use a dedicated synthetic identity.** Upload needs `--allow-materials-upload` and touches only documents whose
  titles match the synthetic corpus. Set `EVAL_FORBIDDEN_OID_SHA256` and `EVAL_EXPECTED_OID_SHA256` to enforce this.
- **Deployed servers need consent.** A non-loopback URL needs `--allow-remote`. Use staging only, never production.

## Offline checks

```powershell
python .\tools\QualityEval\quality_eval.py validate
python -m unittest discover -s .\tools\QualityEval -p "test_*.py"
```

`validate` checks that there are at least 30 items and every category is present, that each required fact is in its
source document, that each key term is spoken in its item, and that forbidden patterns are valid regular expressions.
`scripts/verify-release.ps1` runs the unit tests in its `quality-eval-offline-tests` stage with every `EVAL_*`
variable hidden.

## Dry run against a local Fake-mode API (no Azure)

```powershell
# Terminal 1: the API with the deterministic Fake provider (Development and loopback only)
$env:ASPNETCORE_ENVIRONMENT = 'Development'; $env:Provider__Mode = 'Fake'; $env:ASPNETCORE_URLS = 'http://localhost:5080'
dotnet run --project .\src\VoiceAssistant.Api\VoiceAssistant.Api.csproj
# Terminal 2
python .\tools\QualityEval\quality_eval.py run --base-url http://localhost:5080 `
  --audio-source synthetic-tone --repeats 1 --allow-materials-upload --cleanup-materials
```

The dry run tests the plumbing only. Fake recognition ignores the words, so fact, term and retrieval scores are
meaningless.

## Real run against staging

Set these environment variables:

| Variable | Purpose |
| --- | --- |
| `EVAL_BASE_URL` (or `--base-url`) | Staging app origin |
| `EVAL_DEMO_USERNAME`, `EVAL_DEMO_PASSWORD` | Demo-login credentials of the synthetic identity; **or** |
| `EVAL_BEARER_TOKEN` | A delegated `Meeting.Access` access token of the synthetic Entra user |
| `EVAL_FORBIDDEN_OID_SHA256` | Comma-separated SHA-256 hashes of identities that must never run evaluations |
| `EVAL_EXPECTED_OID_SHA256` | Optional SHA-256 hash of the synthetic identity; any other identity is refused |
| `EVAL_SPEECH_ENDPOINT` | Custom-domain endpoint of a Speech resource for TTS (or `EVAL_SPEECH_REGION`) |
| `EVAL_SPEECH_RESOURCE_ID` | ARM ID of that Speech resource (Entra auth uses `Bearer aad#<resourceId>#<token>`) |
| `EVAL_SPEECH_TOKEN` | Optional Entra token for `https://cognitiveservices.azure.com/.default`; otherwise `azure-identity` or the Azure CLI |
| `EVAL_JUDGE_ENDPOINT`, `EVAL_JUDGE_DEPLOYMENT` | Optional judge: an Azure OpenAI endpoint and a deployment **different** from the one under test |
| `EVAL_JUDGE_API_VERSION`, `EVAL_JUDGE_TOKEN`, `EVAL_SUT_DEPLOYMENT` | Optional judge API version (default `2024-10-21`), token, and the evaluated deployment, which the judge must not match |

To hash an object ID: `python -c "import hashlib,sys;print(hashlib.sha256(sys.argv[1].lower().encode()).hexdigest())" <oid>`.

Permissions: the synthetic identity needs `Meeting.Access` (or demo login). The operator needs
**Cognitive Services Speech User** on the TTS resource and, for the judge, **Cognitive Services OpenAI User**. The
Origin header must be in the server's `Security__AllowedOrigins`; use `--origin` for a revision URL with another host.

```powershell
python .\tools\QualityEval\quality_eval.py synthesize --allow-tts      # once; billable, then cached
python .\tools\QualityEval\quality_eval.py run --allow-remote --allow-materials-upload --repeats 3 --write-details
```

Run options: `--items` and `--categories` (subset); `--accents` (default `en-US,en-GB,en-IN`; `en-AU` also exists);
`--voices file.json`; `--response-mode grounded|balanced|conversation` (default `grounded`, as in `/live.html`);
`--phrases`; `--reply-korean`; `--cleanup-materials`; `--judge`; `--write-details`.

## What is measured

Each session sends `session.start` with the options of `/live.html` and streams the item's timeline as real-time
20 ms frames: lead silence, context turns in two voices, the target question, then silence. Latency starts when the
frame with the question's last speech sample is sent; it includes the upload and excludes browser capture and
rendering. Stages end at `transcript.final`, `response.started`, the first `response.delta`, the delta with
`primaryDone: true`, and `response.completed`. Quality checks are deterministic and token-based: `answeredRate`,
`passRate`, `factRecall`, `forbiddenRate` (negation-aware), `abstainRate`, `formatOkRate`, `genericRate`,
`retrievalHitRate` and `termAccuracy`. p50 is withheld below 5 samples and p95 below 20. CIs come from a cluster
bootstrap over items (2,000 samples, fixed seed).

## Rescore a finished run (offline)

Scores depend on the scorer (`qualityeval/scoring.py`, `qualityeval/textnorm.py`) and on `corpus/items.json`. After
either changes, rescore older runs so that `compare` sees the same scoring on both sides:

```powershell
python .\tools\QualityEval\quality_eval.py rescore C:\eval\baseline\<run folder> --output-dir C:\eval\rescored
```

The input is a run folder written with `--write-details` (or its `details.jsonl`) with `results.json` next to it.
Rescoring is offline. It keeps statuses and latency, and writes a new `<run id>-rescored-<time>-<random>` folder. It
refuses a run whose turn count no longer matches an item.

## Baseline and gate

`compare BASELINE CANDIDATE [--report FILE]` exits **0** (pass), **1** (regression) or **2** (not comparable: a
different corpus, items, scorer, item selection, session options, accents or audio source; `--allow-mismatch`
overrides).

| Rule | Default |
| --- | --- |
| Latency: p95 or p50 of `speechEndToFirstDelta` or `speechEndToCompleted` grows by more than 10% **and** more than 100 ms | `--max-p95-regression 0.10`, `--max-p50-regression 0.10`, `--min-latency-delta-ms 100` |
| Quality: the candidate falls outside the baseline's 95% CI in the bad direction **and** the baseline falls outside the candidate's CI | always on |
| Forbidden claims: the candidate's `forbiddenRate` exceeds the baseline's 95% CI | always on |
| Judge usefulness or support below the baseline CI | advisory; gates only with `--gate-judge` |

Withheld percentiles are skipped, not failed. Run baseline and candidate with the same options and repeats.

## Limitations

- The fictional corpus is small and clean. Scanned PDFs, long decks and near-duplicate documents are not represented.
- TTS speech is clearer than a meeting room, so real recognition is lower.
- The deterministic checks are lexical. A correct paraphrase without the expected phrase fails.
- Latency is measured at the server boundary, not browser-to-screen.
- A full run takes about 20–30 minutes and costs Speech, model and Search usage; see the runbook.
