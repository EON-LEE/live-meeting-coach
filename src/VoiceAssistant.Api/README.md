# API (`VoiceAssistant.Api`)

ASP.NET Core on .NET 10. It serves the browser bundle from `wwwroot` and the same-origin HTTP and WebSocket API. The
protocols and their limits are in [websocket-v1](../../contracts/websocket-v1.md),
[knowledge-v1](../../contracts/knowledge-v1.md) and [practice-v1](../../contracts/practice-v1.md). The Search schema is
[search-index.json](../../contracts/search-index.json).

## Offline development and container build

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'; $env:Provider__Mode = 'Fake'; $env:ASPNETCORE_URLS = 'http://localhost:5080'
dotnet run --project .\src\VoiceAssistant.Api\VoiceAssistant.Api.csproj
dotnet test .\tests\VoiceAssistant.Api.Tests\VoiceAssistant.Api.Tests.csproj
docker build -f .\src\VoiceAssistant.Api\Dockerfile -t voice-assistant .   # from the repository root
```

The fake provider never contacts Azure. It runs only in Development and accepts only loopback clients. The tests use
local Kestrel listeners, deterministic providers and locally signed JWTs; they never call Azure. The image builds the
web bundle and the API into a non-root `aspnet:10.0-noble` image on port 8080. Without access to NuGet.org, pass an
approved credential-free feed with `--build-arg NUGET_SOURCE=<v3 index URL>`.

## Configuration

Settings are environment variables (`__` maps to `:`). Missing or partial settings and invalid values prevent startup.
The API accepts no keys or secrets: `DefaultAzureCredential` uses managed identity in Azure.

| Key | Meaning |
| --- | --- |
| `Provider__Mode` | `Azure` (default); `Fake` requires Development |
| `Authentication__TenantId`, `Authentication__ClientId` | Entra tenant GUID and SPA application (client) GUID |
| `Authentication__Audience`, `Authentication__Scope` | API token `aud` (not the SPA client ID) and the full scope URI ending in `/Meeting.Access` |
| `Security__AllowedOrigins__0` | Exact public HTTPS origin without a trailing slash; more indices are allowed |
| `Azure__SpeechRegion`, `Azure__SpeechResourceId` | Speech region and full ARM resource ID (for Entra authorization) |
| `Azure__SpeechEndpoint` | Optional HTTPS resource endpoint from the portal (custom subdomain); do not append a path |
| `Azure__OpenAIEndpoint`, `Azure__ChatDeployment` | HTTPS Azure OpenAI endpoint; streaming chat deployment for answers, the turn classifier, Korean lines and practice |
| `Azure__ChatMaxOutputTokens` | Completion token budget, 64 to 4096, default 2048; reasoning tokens share it |
| `Azure__SearchEndpoint`, `Azure__SearchIndex` | Optional; set both or neither. Without them grounding is `disabled` |
| `Azure__EmbeddingDeployment` | Required with Search; `text-embedding-3-small`, 1536 dimensions |
| `Azure__SearchSemanticConfiguration` | Default `meeting-semantic` (title and content) |
| `Azure__SearchMinimumRerankerScore` | Reranker cutoff, 0 to 4; see the [quality baseline](../../docs/runbooks/quality-baseline.md) for the values in use |
| `Session__MaxMinutes` | Session limit in minutes, 5 to 180, default 30 |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Optional; turns on [metric export](#content-free-timing-metrics) |
| `DemoLogin__*` | Optional shared demo login; see [infra](../../infra/README.md#optional-demo-login-post-apidemologin) |

### Experiment switches

Each switch changes one behavior, so it can be compared on staging with [QualityEval](../../tools/QualityEval/README.md).
The API keeps every switch off unless configured; the deployment template (`infra/main.bicep`) turns the first four
on. Startup logs the values in effect (`Live reply switches: ...`).

| Key | Values (API default) | Effect |
| --- | --- | --- |
| `Session__EarlyStartAtFinal` | `true`, `false` (`false`) | With `replySettleMs` above 0, a final that completes a question or request starts its reply at once instead of after the settle window. Later speech that changes the target replaces that reply |
| `Session__SpeculativeStart` | `true`, `false` (`false`) | Requires `Session__EarlyStartAtFinal`. When the latest partial stays unchanged for 250 ms and completes a question or request, one reply is generated and held on the server. The final with the same words releases it; otherwise it is discarded unseen |
| `Session__QuietReplyMs` | `0`, or 500 to 10000 (`0`) | With `replyOn` `all`, a short statement that the settle window kept waiting on is answered after this much further silence. The template uses 1500 |
| `Speech__MaterialPhraseHints` | `true`, `false` (`false`) | Adds up to 100 titles and terms from the caller's own materials (acronyms, product codes, repeated names) to the recognizer phrase list, after the client's phrases. Cached per user for 30 minutes; never logged |
| `Azure__SearchMaxEvidence` | 1 to 10 (5) | The most chunks at or above the reranker cutoff that one reply may use |
| `Azure__AnswerDeployment` | deployment name (unset) | A separate deployment for the live answer only. The template sets it only with `useAnswerDeployment` (off by default) |
| `Azure__ChatReasoningEffort` | `minimal`, `low`, `medium` (unset) | `reasoning_effort` of the live answer request. A rejected value fails the reply with `response_unavailable`; nothing falls back |
| `Azure__ChatStreamUsage` | `true`, `false` (`false`) | The live answer asks for `stream_options.include_usage`, so its tokens are counted in `voiceassistant.chat.tokens` |
| `Azure__ChatServiceTier` | `auto`, `default`, `flex`, `priority`, `scale` (unset) | `service_tier` of the live answer request only |
| `Azure__ChatPromptCacheKey` | `off`, `static`, `session` (`off`) | `prompt_cache_key` of the live answer: one constant per deployment and prompt version, or a random key per session. Never derived from the user |
| `Reply__RouteInFocus` | `true`, `false` (`false`) | The answer prompt names the route in its response focus and puts the search-failure instructions after the evidence, so a session's replies share a longer unchanging prefix |

With a switch off, the API sends the request exactly as before. Enable one switch at a time on staging first; a value
the deployment rejects fails the reply.

## Runtime behavior

- **Identity.** The runtime identity needs Cognitive Services Speech User, Cognitive Services OpenAI User and, with
  Search, Search Index Data Contributor. Speech needs a custom subdomain for Entra authentication.
- **One replica.** Tickets, session slots, upload admission and conversation resume live in process memory.
- **Headers.** `no-store`, `nosniff`, `no-referrer`, a restrictive `Permissions-Policy`, CSP `script-src 'self'`, HSTS.
- **Health.** `/health/live` reports that the process runs. `/health/ready` is 503 `warming` until the token service,
  Azure OpenAI and Speech answer, then 200 `ready`, or `not_ready` after 60 s of failures. Search warm-up is
  best-effort. Any HTTP status counts as an answer, so monitor RBAC separately.
- **Grounding.** Search uses the per-user ACL prefilter and semantic reranking; only chunks at or above the cutoff are
  evidence. No match gives `no_matches` and a conversation-only reply. A Search failure (after one retry within
  800 ms) gives `grounding_unavailable` and a brief non-committal reply.
- **Sessions.** One per user and 100 in total; a new session supersedes the old one. At `Session__MaxMinutes` the
  server sends `session_time_limit`. A retryable Speech cancellation restarts the recognizer, at most 3 times.
- **Live chat retry.** The answer, classifier and Korean calls retry a 429 or transient failure once, only for a
  requested wait of at most 1 s with 2 s of deadline left. A filtered reply is withdrawn (`response_filtered`).
- **Conversation resume.** With `resumeConversation`, a reconnect within 10 minutes continues the finalized history
  (at most 12 turns, in memory only).
- **Data.** The API never logs or persists transcripts, audio, queries, request bodies, notes or credentials. Azure
  services process content under their own policies. Keep full URLs out of ingress logs: they carry one-use tickets.
- **Certificates.** The Speech SDK (pinned in the csproj) checks certificate revocation and fails closed.

## Content-free timing metrics

The `VoiceAssistant.Api` meter exposes these instruments. Durations are milliseconds from the reply's own final STT
callback unless stated otherwise; they are not speech-end latency. Export to Application Insights happens only when
`APPLICATIONINSIGHTS_CONNECTION_STRING` is set, through a `MeterProvider` limited to this meter, with Entra
authentication. The cloud role is `OTEL_SERVICE_NAME` (default `voice-web`). Statsbeat is off.

| Instrument | Type | Extra tags | Meaning |
| --- | --- | --- | --- |
| `voiceassistant.stt_final_to_first_delta` | histogram | reply tags | Final STT **callback** to first successfully sent model response delta |
| `voiceassistant.stt_final_to_completed` | histogram | reply tags | Final STT callback to successfully sent model response completion |
| `voiceassistant.reply.settle_wait` | histogram | reply tags without `grounding` | Final to the reply starting: the settle window plus any wait behind an active reply (manual: until the request) |
| `voiceassistant.reply.retrieval_wait` | histogram | reply tags, `prefetched=true\|false` | Knowledge route only: how long the started reply still waited for its documents |
| `voiceassistant.reply.first_token` | histogram | reply tags | Answer request sent to the first model text (not from the final) |
| `voiceassistant.reply.final_to_primary_done` | histogram | reply tags | Final to the sent delta that carries `primaryDone` |
| `voiceassistant.reply.classifier_wait` | histogram | reply tags, `outcome=respond\|listen\|timeout\|failed` | How long the finished answer waited for the turn classifier before `response.completed` |
| `voiceassistant.reply.korean_ready` | histogram | reply tags, `line=primary\|alternative`, `outcome=translated\|failed` | Final to each sent `response.korean` |
| `voiceassistant.retrieval_duration` | histogram | `provider`, `outcome` | Retrieval call elapsed time, including disabled/no-match/error/cancellation paths |
| `voiceassistant.reply.prefetch` | counter | `result=hit\|miss` | Knowledge replies that reused a prefetched retrieval or started their own |
| `voiceassistant.reply.grounding` | counter | reply tags | Completed replies (deterministic ones included) by final grounding status |
| `voiceassistant.retrieval.failures` | counter | `class=transient\|permanent`, `kind`, `status_class=4xx\|5xx\|none\|...`, `retried=true\|false` | Retrievals that finally failed |
| `voiceassistant.chat.throttled` | counter | `operation=answer\|classifier\|translation` | HTTP 429 answers to live chat calls |
| `voiceassistant.chat.retries` | counter | `operation` | Live chat calls that were retried |
| `voiceassistant.session.errors` | counter | `code` | `error` events sent to clients, by error code; `code=response_filtered` counts replies the content filter stopped |
| `voiceassistant.session.ended` | counter | `reason` | Ended sessions: `stopped`, `client_closed`, `superseded`, `overloaded`, `time_limit`, `aborted`, `ended`, `startup_timeout`, `transport_error`, `provider_unavailable` or the ending error code |
| `voiceassistant.speech.restarts` | counter | `code=connection_failure\|service_timeout\|service_unavailable`, `outcome=restarted\|failed` | In-process speech recognizer restart attempts after a retryable cancellation |
| `voiceassistant.warmup.failures` | counter | `stage`, `required=true\|false` | Failed startup warm-up attempts; `required=false` is the best-effort Search warm-up |
| `voiceassistant.reply.speculation` | counter | `provider`, `route`, `response_mode`, `outcome=started\|adopted\|discarded`, `reason=mismatch\|target_changed\|cancelled` (discarded only) | Speculative replies (`Session__SpeculativeStart`): started on a paused partial, adopted by their final, or discarded |
| `voiceassistant.reply.speculative_head_start` | histogram | `provider`, `route`, `response_mode` | Adopted speculative replies: their final's time minus the speculative start |
| `voiceassistant.session.resume` | counter | `outcome=restored\|expired\|none` | Meeting session starts that asked to continue the previous conversation (`resumeConversation`) |
| `voiceassistant.session.resumed_turns` | histogram (`{turn}`) | none | Finalized turns a resumed session continued with |
| `voiceassistant.sessions.active` | observable gauge | none | Open meeting sessions |
| `voiceassistant.chat.tokens` | counter (`{token}`) | `operation=answer\|classifier\|translation`, `type=input\|cached\|output\|reasoning` | Chat tokens; the answer is counted only with `Azure__ChatStreamUsage` |
| `voiceassistant.speech.recognition_lag` | histogram | `kind=partial\|final` | Audio already sent to the recognizer past the end of the recognized phrase when its result arrives |
| `voiceassistant.reply.outcomes` | counter | `start_reason=manual\|no_settle\|early_final\|settle\|supersede\|quiet`, `classifier=respond\|listen\|timeout\|failed\|none`, `outcome=completed\|fallback\|cancelled\|error` | Every reply, once, with why it started and how it ended |
| `voiceassistant.reply.queue_wait` | histogram | reply tags without `grounding` | Queued replies: how long they waited behind an active reply |
| `voiceassistant.reply.decision` | counter | `decision=respond\|listen\|unknown` | The classifier verdict known when the first line was sent (`decision` on `response.delta`) |
| `voiceassistant.reply.feedback` | counter | `reason` | `response.feedback` reports from the live page |
| `voiceassistant.reply.visible` | histogram | none | `response.viewed` reports: how long a reply stayed visible on screen |

Reply tags are `provider=Azure|Fake|TestDouble`, `trigger=automatic|manual`, `route=knowledge|transcript|profile`,
`response_mode=balanced|grounded|conversation` and `grounding=grounded|no_matches|unavailable|disabled|pending`. Tag
values come from fixed vocabularies; no content, user ID or URL is a tag. Never mix Fake and Azure measurements.

Each reply also writes one content-free log line, `ReplySummary outcome=... start_reason=... classifier=... decision=...
waits=... quiet_armed=... queued=... queue_wait_ms=... speculation=... spec_edit=... speech_restarts=... first_line_ms=...`.
It reaches Log Analytics only when the container app streams console logs (`enableDiagnostics`). The
[workbook](../../infra/README.md#workbook) charts these metrics.
