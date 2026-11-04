# Live Coach

Real-time English meeting reply assistant on Azure.

This repository is `live-meeting-coach` (formerly `voice-assistant`). Project names and namespaces (`VoiceAssistant.*`),
the container image repository `voice-assistant` and Azure resource names are intentionally unchanged.

Live Coach listens to meeting audio that you explicitly share. Azure AI Speech transcribes it. Azure AI Search finds
relevant passages in your own uploaded materials, filtered to your account. Azure OpenAI streams two short English
reply suggestions for you to read aloud, each with an optional Korean meaning. Live Coach never sends meeting messages
and never makes commitments for you.

## Clients

- **Browser.** `/live.html` is the compact live page. `/index.html` hosts the Meeting and Practice pages.
  `/materials.html` manages your meeting materials. No installer or browser extension is needed.
- **Windows overlay.** `src/VoiceAssistant.WebOverlay` publishes `LiveCoach.exe`, which hosts `/live.html` in a
  transparent, always-on-top WebView2 window. It can also capture only the Teams desktop app's audio (**Teams 앱 소리**,
  Windows 10 build 20348 or later). By default the window asks Windows to exclude it from screen capture. Launching it
  does not capture audio. See the [WebOverlay README](src/VoiceAssistant.WebOverlay/README.md).

## Live page

The live page shows the completed suggestions at the top: **추천 1** and, when the reply has a second line,
**추천 2 · 다른 방향**, with the question they answer. Streaming drafts never replace the sentence you are reading.
**답변 고정** holds the current answer while you read; capture and replies continue. **일시정지** stops sending audio.
Grounding warnings and connection errors stay visible. Source details are under **상세 근거**.

| Control | Effect | Default |
| --- | --- | --- |
| 문서검색 **사용 / 미사용** | Search your uploaded materials, or answer from the conversation only (fastest) | 사용 |
| 추천 **모든 발언 / 질문만** | Suggest a reply for every meaningful utterance, or only for questions and direct requests | 모든 발언 |
| **자막** | Show the recognized conversation | off |
| **한국어 뜻** | Show the Korean meaning of the suggestions; off sends no translation requests | off |
| **내 메모** | Your position, constraints or goals for this meeting (up to 500 characters). Both suggestions follow them. The server uses them only for that request and never stores or logs them | no notes |

The choices are kept in this browser or WebOverlay profile. When the server supports `session.update`, a change
applies from the next reply. Otherwise it applies from the next connection.

The two meeting pages keep separate defaults, and settings do not transfer between them. The
[Web README](src/VoiceAssistant.Web/README.md) describes both pages and the offline audio demo.

| Page | Session defaults | Korean assist (off by default) |
| --- | --- | --- |
| `/live.html` | Search on every reply (`grounded`), semantic segmentation, 1,100 ms silence fallback | Translation only |
| `/index.html` | Search when needed (`balanced`), 500 ms end silence | Translation plus an approximate Hangul reading |

## Architecture

```text
Meeting audio (browser tab, Teams app through LiveCoach.exe, or microphone)
  -> PCM16 audio over an authenticated WebSocket -> ASP.NET Core API
  -> Azure AI Speech streaming recognition
  -> conversation context + optional Azure AI Search (per-user ACL filter, semantic reranking)
  -> Azure OpenAI streaming reply -> live page
```

The browser needs HTTPS (or localhost), a user gesture and permission to share. Teams in an Edge or Chrome tab with
**Share tab audio** is the simplest path; a shared window alone may have no audio track. Only audio is sent to the
server. If the shared source has no audio track, the app stops and says so; it never silently switches to the
microphone. The wire protocol is [`contracts/websocket-v1.md`](contracts/websocket-v1.md).

## Materials, in-person meetings and practice

- **My meeting materials.** Open `/materials.html` to upload notes, minutes, slides (`.pptx`), documents (`.docx`, text
  `.pdf`), text, Markdown, transcripts (`.vtt`, `.srt`) and code, or to paste notes. Only extracted text chunks and
  embeddings are stored, visible only to your Entra object ID, until you delete them. Original files are not stored
  ([`contracts/knowledge-v1.md`](contracts/knowledge-v1.md)).
- **Microphone mode.** Choose the microphone as the audio source for an in-person meeting. Room audio is sent to Azure,
  so inform participants as your policy requires.
- **Session length.** The deployment setting `sessionMaxMinutes` limits a session (template 180 minutes, API default
  30). At the limit, `/live.html` starts a new session; on `/index.html`, click Start again.
- **Korean assist and practice.** Korean assist is off by default, and read-aloud audio plays only when you ask for
  it. The Practice page runs a stateless AI partner round with Korean feedback on wording
  ([`contracts/practice-v1.md`](contracts/practice-v1.md)).

## Build and test

Contributors need Node.js 22.12 or later and the .NET 10 SDK.

```powershell
.\scripts\test-local.ps1                       # build and test .NET; -Dotnet <path> selects an isolated SDK
$env:VOICE_ASSISTANT_BACKEND_E2E = '1'
.\scripts\test-web.ps1 -InstallBrowsers        # web bundle, unit tests, Chromium tests with the real local API
Set-Location .\src\VoiceAssistant.Web; npm ci; npm run dev -- --host 127.0.0.1 --port 5173
dotnet publish .\src\VoiceAssistant.WebOverlay -c Release -r win-x64 --self-contained true -o C:\Apps\LiveCoach
```

The development server serves `http://127.0.0.1:5173/live.html` and `/index.html`. The fake provider is for
Development on loopback only and never replaces a failed Azure connection. The last command publishes `LiveCoach.exe`.

To run the API, browser and infrastructure checks as one fail-closed run, write a report outside the repository:

```powershell
.\scripts\verify-release.ps1 -BicepPath 'C:\tools\bicep.exe' -ReportPath 'C:\artifacts\verification.json' -InstallBrowsers
```

The report records the revision and each stage result without meeting content or credentials. Local success is
`LOCAL_PASS_LIVE_NOT_VERIFIED`, never a claim of working cloud inference; with `-RequireLive` the run ends `BLOCKED`
because it cannot verify Azure. To test a published bundle on a running local server, set
`VOICE_ASSISTANT_WEB_URL='http://localhost:5081'`, `VOICE_ASSISTANT_BACKEND_E2E='1'` and
`VOICE_ASSISTANT_API_EXTERNAL='1'`, then run `npm test` in `tests\VoiceAssistant.Web.E2E`.

**Build and test** (`.github/workflows/ci.yml`) runs on every push to `main` and every pull request; read test results
from its latest run. Its artifacts are `verification-evidence` (the `verify-release.ps1` report), `test-results` (.NET
TRX), and `browser-test-report` and `linux-browser-test-report` (Playwright, Windows and non-root Linux).

Security automation: **CodeQL** (`codeql.yml`) scans C#, JavaScript/TypeScript, Python and workflows on pushes, pull
requests and weekly. **Secret scan** (`secret-scan.yml`) runs gitleaks over the full history (reviewed exceptions are in
`.gitleaksignore`). **Dependabot** proposes weekly NuGet, npm, Docker and GitHub Actions updates.

## Responsible meeting use

- Capture starts only after you choose to listen. Follow meeting notice, consent and company policy. Do not bypass
  protected audio or tenant restrictions.
- Without a separate microphone path, the system does not know what you said. A displayed suggestion is not proof that
  you said it.
- Share a single window rather than the whole display; exclusion of the overlay from capture is not guaranteed.
- Suggestions must not invent personal experience, project facts, deadlines or commitments. Missing evidence leads to
  a request to confirm.

## Azure prerequisites

Deployment needs an existing resource group and registry, a pushed image digest, approved Entra API and SPA app
registrations, and model capacity; it never reuses another application's identity, documents or deployments. Check
model availability, quotas, roles, pricing and permitted regions first. The target is configured in the git-ignored
`infra/release.local.json`; see [Local release target](infra/README.md#local-release-target-inforeleaselocaljson).

- **Authentication.** The browser uses Microsoft Entra authorization code with PKCE and keeps tokens in memory. A
  same-origin endpoint issues a short-lived, one-use WebSocket ticket, so access tokens never appear in URLs. Azure
  services use managed identity; keys never reach the browser. Tickets live in memory, so the app runs one replica.
- **Knowledge.** Search results are filtered by the signed-in user's permissions. Retrieved documents are evidence,
  never instructions.
- **Microsoft 365.** An Azure subscription grants no access to mail, Teams or SharePoint content. Work IQ or other
  Microsoft 365 integration needs supported APIs, licensing and tenant authorization.
- **Cost.** Speech time, model tokens, Search capacity and the API replica drive cost. Budget alerts are not limits.

## Repository map

| Location | Contents | Documentation |
| --- | --- | --- |
| `src/VoiceAssistant.Web` | Live, Meeting, Practice and materials pages; audio capture | [Web README](src/VoiceAssistant.Web/README.md) |
| `src/VoiceAssistant.WebOverlay`, `src/VoiceAssistant.Audio` | `LiveCoach.exe` overlay; native Teams audio capture and 16 kHz PCM16 framing | [WebOverlay README](src/VoiceAssistant.WebOverlay/README.md) |
| `src/VoiceAssistant.Api` | Authentication, recognition, retrieval, streaming replies, materials and practice APIs | [Api README](src/VoiceAssistant.Api/README.md): configuration, switches, metrics |
| `contracts` | Client/server protocols and the Search index schema | [WebSocket](contracts/websocket-v1.md), [knowledge](contracts/knowledge-v1.md), [practice](contracts/practice-v1.md) |
| `infra`, `scripts/infra` | Bicep deployment, knowledge import, [demo login](infra/README.md#optional-demo-login-post-apidemologin), private ingestion | [infra README](infra/README.md), [private ingestion](infra/private-ingestion/README.md) |
| `scripts/release` | Staged image release and promotion | [Release script](scripts/release/README.md) |
| `docs/runbooks` | Operations | [Release and rollback](docs/runbooks/release-and-rollback.md), [capture troubleshooting](docs/runbooks/capture-troubleshooting.md), [Search semantic quota](docs/runbooks/search-semantic-quota.md), [quality baseline](docs/runbooks/quality-baseline.md) |
| `tools/QualityEval` | Synthetic end-to-end quality and latency gate | [QualityEval README](tools/QualityEval/README.md) |
| `tests` | API, web unit, browser and WebOverlay tests | |
