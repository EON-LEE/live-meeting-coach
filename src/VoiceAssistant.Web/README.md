# Web client (`VoiceAssistant.Web`)

TypeScript and Vite. The API serves the built pages from the same origin, so users only open a URL. `LiveCoach.exe`
([WebOverlay README](../VoiceAssistant.WebOverlay/README.md)) shows `/live.html` in an always-on-top WebView2 window.
Only the in-person microphone mode asks for microphone permission. The protocol is in
[websocket-v1](../../contracts/websocket-v1.md).

| Page | Purpose | Defaults | What persists |
| --- | --- | --- | --- |
| `/live.html` | Compact live panel, also inside `LiveCoach.exe` | Document search `grounded`, replies to all speech, captions off, Korean off, no notes | `live-coach-settings` (quick settings, source, topic, notes, answer size) and the MSAL cache of this page |
| `/index.html` | Meeting and Practice pages | Document search `balanced`, 500 ms end silence, Korean off | Only the overlay opacity; sign-in and settings stay in memory |
| `/materials.html` | Upload and manage the user's own materials | | Nothing; the token stays in tab memory |

The pages have separate settings. Do not infer one page's defaults or stored state from another.

## Live page (`/live.html`)

- **Layout.** A dark panel shows two reply suggestions at the top: **추천 1** and a quieter **추천 2 · 다른 방향**, each
  with its Korean meaning when 한국어 뜻 is on. A `response.delta` with `primaryDone:true` shows the first line early.
  The fold (**상세 근거**) holds what was heard, the grounding detail and the sources. **A− / A+** scale the answers.
- **Controls.** The header has **시작 / 종료** and **일시정지 / 다시 듣기**. The right-click menu (or Shift+F10) has
  settings, materials, start, pause, regenerate, Korean retry, stop and the always-on-top picture-in-picture window.
- **Quick row.** **문서검색 사용/미사용** switches between the Settings mode (`grounded` or `balanced`) and
  `conversation`. **추천 모든 발언/질문만** sets `replyOn`. **자막** shows the transcript locally. **내 메모** opens the
  notes field (up to 500 characters, sent as `notes`, used only in the answer prompt). If `session.ready` announces
  `sessionUpdate`, a change applies from the next reply; otherwise it applies at the next connection.
- **Korean meaning.** With 한국어 뜻 on, the server pushes one `response.korean` per reply line (`replyKorean:true`).
  If a line fails or takes over 4 s, the page translates it through `/api/assist/enrich`. Off means no requests.
- **Reconnect without re-sharing.** One audio source lives for the whole live session and starts only from a click. A
  dropped connection reconnects only the server session (300 ms to 3 s backoff, 20 attempts) and then sends the
  newest 4 s of audio. `session_time_limit` rolls over to a new session with the same source. Later connections send
  `resumeConversation:true`; a new Start begins a fresh conversation. Pause sends a silent frame every 20 s.
- **Failures.** `live-errors.ts` maps error codes to reconnect, rollover, notice or stop (with a Korean recovery
  button). `grounding_unavailable` shows **자료 확인 불가**; `response_filtered` removes the stopped reply.
- **Auto-hold.** When the server marks a reply `decision:"listen"` (the other person is still talking), the answer on
  screen stays and the status reads **상대가 말하는 중 · 이전 답변 유지**; the new reply waits behind **새 답변 보기**.
  The next reply without `listen` ends this hold. A hold the user set (**답변 고정**) is never released automatically.
- **Feedback.** Under the answers, **좋아요** or **아쉬워요** and a reason (늦음, 틀림, 부족, 읽기 어려움, 불필요, 필요한 답 없음)
  sends one `response.feedback` per reason and reply. The row appears only when `session.ready` announces `feedback`.
- **On-screen time.** When `session.ready` announces `viewed`, `live-dwell.ts` reports how long each reply stayed
  visible (`response.viewed`), at most every 5 s and when the page hides or stops. Nothing is counted while it is hidden.
- **Start hint.** Before a start with neither a topic nor notes, the **내 메모** chip is highlighted with a one-line
  example of notes.
- **LiveCoach audio.** Inside `LiveCoach.exe` the source list adds **Teams 앱 소리** (the default there; Windows build
  20348 or later) and **PC 전체 소리** through `src/host-audio.ts`, with no picker.

## Meeting and Practice pages (`/index.html`)

The page is one floating card with Start/Stop, a timer, the Meeting/Practice switch and a Settings sheet. **Float**
moves the same card into a Document Picture-in-Picture window (Chrome, Edge); it stays on top but is not transparent.
Opacity (35 % to 100 %, default 80 %) is the only stored value (`voice-assistant.overlay-opacity`).

**Audio demo.** The page starts in **Audio demo**. **Start demo** plays the bundled original recording
(`src/assets/demo-original.wav`) and a scripted transcript and reply, with no sign-in, microphone or backend call.
Select **Live** to use Azure; a failed live connection never falls back to the demo.

**Meeting options.** These values stay in page memory and are sent in `session.start` only:

| Setting | Values |
| --- | --- |
| Document search (`responseMode`) | `balanced` (default), `grounded` or `conversation`; changes apply mid-session only with `sessionUpdate` |
| Profile and topic | `name` up to 100, `role` up to 160, `project` and the meeting topic up to 300 characters; a profile requires the confirmation checkbox |
| Recognition terms | One per line; up to 40 terms of 64 characters, 2048 characters in total |
| End-of-turn silence | 450, 500 (default), 700 or 1000 ms; the API accepts 350 to 1500 |

The page does not send `replyOn`, `notes` or `resumeConversation`, and it does not reconnect automatically.

**Korean assist and listening.** The Korean checkbox is off by default. When on, the page calls `/api/assist/enrich`
after `response.completed` for the Korean meaning and an approximate Hangul reading. **듣기** calls
`/api/assist/speak` (fast, normal or slow; up to 400 characters). Replies never play automatically.

**Practice.** Choose a scenario, difficulty 1 to 3 and 1 to 12 questions (default 5), then give consent. The
microphone runs with `transcribeOnly:true`: no automatic reply or retrieval. Each round follows
[practice-v1](../../contracts/practice-v1.md). Feedback covers the recognized wording, not pronunciation or a grade.

## Audio capture and consent

- **Teams in a browser.** Choose **Meeting tab / system audio**, then the Teams tab with **Share tab audio**.
- **Teams desktop app.** Share the whole screen with **Share system audio** (the loopback of the default output
  device; sound routed to another device is not captured). A window share has no audio.
- **In person.** Choose **Microphone (in-person)** and tell the participants first. The page calls `getUserMedia`
  with echo cancellation, noise suppression and gain control from the Start click.
- `getDisplayMedia` runs only from a click. Video is requested to get display audio but is never read or sent. A
  missing audio track fails visibly, with no microphone fallback. Stop, failure and unload end every track.
- An AudioWorklet downmixes to mono, filters, resamples to 16 kHz and sends 20 ms PCM16 frames after `session.ready`.
  `/index.html` stops when the socket backlog exceeds 96,000 bytes; `/live.html` keeps 4 s and reconnects.
- **Pause** (or **P** outside text fields) drops audio locally and cancels suggestions. Audio already sent cannot be
  recalled. No browser permission replaces participant consent or meeting policy.

## My meeting materials (`/materials.html`)

The meeting page opens this page in a new tab, so a running meeting continues. It signs in separately. Choose files
or a folder, drag and drop them, or paste a note (title 1 to 200 characters, text up to 400,000). The page skips
hidden and build folders, unsupported types, files above 5 MiB and files that are not UTF-8 text. A selection holds
at most 300 files. Uploads run two at a time and can be retried or cancelled one by one. Every delete asks for
confirmation. Supported types and extraction rules are in [knowledge-v1](../../contracts/knowledge-v1.md).

Put one topic in each file and write key facts as text: text inside images is not read, and a PDF needs a text layer.
**The extracted text and its embeddings stay in your Search index until you delete them. The original file is not
stored. Upload only what you may process in Azure.**

## Browser authentication

Live mode loads `GET /api/client-config` (format in [websocket-v1](../../contracts/websocket-v1.md)). Register an
Entra SPA redirect URI equal to the deployed origin and grant the delegated scope. MSAL Browser uses authorization
code with PKCE and a sign-in popup opened by a click. `/index.html` and `/materials.html` keep tokens in memory;
`/live.html` keeps its MSAL cache in `localStorage`. The page exchanges the token for a one-use ticket and opens the
WebSocket with it; no token goes into a URL. The materials popup uses the origin-root redirect URI and the opener
completes it, so the root page must not call `handleRedirectPromise`. Missing configuration, failed sign-in or a
failed socket fails visibly and releases capture.

## Development

```powershell
Set-Location .\src\VoiceAssistant.Web
npm ci; npm run typecheck; npm test; npm run build
npm run dev   # http://127.0.0.1:5173; proxies /api and /health to http://localhost:5080 (VOICE_ASSISTANT_DEV_API)
```

Use Node 22.12 or later. `npm run build` writes the three pages and `dist/assets`. `dotnet publish` does not build
them: the API Dockerfile copies the whole `dist` into `wwwroot`, and a local publish test must do the same.

**Local fake backend.** Run the API with `Provider__Mode=Fake` in Development ([API](../VoiceAssistant.Api/README.md)).
On a loopback host the page offers **Local fake backend · developer test only**, which sends a synthetic tone.

**Tests.** `npm test` runs the Node tests and the Vitest tests in `tests/VoiceAssistant.Web.Tests`. The Playwright
suite is in `tests/VoiceAssistant.Web.E2E` (`npm test` there). No test calls Azure, a real identity provider, a real
microphone or a screen picker. Optional environment variables:

| Variable | Effect |
| --- | --- |
| `VOICE_ASSISTANT_BACKEND_E2E=1` | Runs the cases against the actual local Fake API |
| `VOICE_ASSISTANT_API_EXTERNAL=1`, `VOICE_ASSISTANT_WEB_URL` | Uses an API and web origin that are already running |
| `VOICE_ASSISTANT_KNOWLEDGE_E2E=1`, `VOICE_ASSISTANT_PRACTICE_E2E=1` | Add real Fake knowledge uploads and a real Fake practice round |
| `VOICE_ASSISTANT_VITE_AUTH_TESTS=1` | Runs the injected-auth tests that need the Vite dev server |
| `VOICE_ASSISTANT_COACH_SCREENSHOTS`, `VOICE_ASSISTANT_OVERLAY_SCREENSHOTS` | Absolute folder outside the repository for screenshots |

**Debugging.** `html[data-live-ready="1"]` marks a loaded live page. With `?debug=1` or localStorage `live-coach-debug`
= `1`, `window.__liveCoachDebug` shows reconnect and stall counters and reading latency. Nothing is sent anywhere.
