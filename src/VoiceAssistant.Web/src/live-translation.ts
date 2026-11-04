import { CoachError, type CoachClient, type CoachKind } from "./coach-client.js";
import { answers, translatable, translationChunks, type LiveReply, type LiveState, type LiveTurn } from "./live-state.js";
import type { ServerEvent } from "./protocol.js";
import { PushedKorean, pushedKoreanWaitMs } from "./reply-korean.js";

export interface TranslationDeps {
  coach: Pick<CoachClient, "translate">;
  state: LiveState;
  /** Korean meanings are on. Off means no Korean is requested, pushed or translated at all. */
  korean(): boolean;
  /** The heard conversation is shown; only then do its turns get Korean. */
  captions(): boolean;
  /** Whether a line the server pushed is usable Korean; one that is not is translated by the client instead. */
  acceptsPushed(korean: string): boolean;
  /** Something on screen changed. */
  changed(): void;
}

/** How many recent heard turns get Korean when Korean or captions are switched on. */
const recentTranslatedTurns = 6;

function retryable(error: unknown): boolean {
  if (error instanceof CoachError) return ["busy", "provider_unavailable", "provider_timeout"].includes(error.code);
  return error instanceof TypeError || (error instanceof Error && error.name === "TimeoutError");
}
function failureLabel(error: unknown): string {
  const code = error instanceof CoachError ? error.code : "";
  return code === "busy" ? "번역 요청 제한 · 잠시 후 재시도" : code === "provider_timeout" ? "한국어 번역 시간 초과"
    : code === "unauthorized" || code === "forbidden" ? "한국어 번역 인증 오류" : "한국어 번역 실패";
}

/**
 * The Korean meanings of the live page (translation-only, retried on transient failure): of reply lines, which the server may
 * push with each line (`response.korean`), and of heard turns, which the client always translates itself.
 */
export class LiveTranslations {
  private requests = new AbortController();
  private readonly meetingKo = new Map<string, string>();
  private readonly failedTurns = new Map<string, string>();
  private readonly requestedTurns = new Set<string>();
  private readonly replyKo = new Map<string, string[]>();
  // The server pushes a Korean line with each reply line; a line it cannot provide falls back to the translation request.
  private readonly pushes: PushedKorean;
  // Only a reply that began while the server was pushing Korean has a pushed line to wait for.
  private readonly pushedAtStart = new Set<string>();

  constructor(private readonly deps: TranslationDeps) {
    this.pushes = new PushedKorean(deps.acceptsPushed);
  }

  /** A heard turn whose translation failed can be retried. */
  get hasFailedTurns(): boolean { return this.failedTurns.size > 0; }

  /** Stops all Korean work at once and forgets it: running requests end and no "translating…" line stays behind. */
  clear(): void {
    this.requests.abort(); this.requests = new AbortController();
    this.meetingKo.clear(); this.failedTurns.clear(); this.requestedTurns.clear(); this.replyKo.clear();
    this.pushes.clear(); this.pushedAtStart.clear();
  }
  /** A pushed line (`response.korean`). */
  record(e: ServerEvent): void { this.pushes.record(e); }
  /** A reply began while the server pushes Korean, so its lines are worth a short wait. */
  started(responseId: string): void { this.pushedAtStart.add(responseId); }
  /** The connection ended, so no pushed line arrives any more: waiting lines are translated now. */
  expire(): void { this.pushes.expire(); }

  /** The Korean of the reply's lines, as far as known. */
  replyLines(responseId: string): readonly string[] { return this.replyKo.get(responseId) ?? []; }
  /** A line the server already pushed for the reply (`null`: it has none). */
  pushed(responseId: string, index: number): string | null | undefined { return this.pushes.peek(responseId, index); }

  /** The Korean line shown under a heard turn; `undefined` means the turn has none (Korean off, or never requested). */
  turnKorean(turn: LiveTurn): string | undefined {
    if (!this.deps.korean()) return undefined;
    if (!turn.final) return "듣는 중…";
    return this.meetingKo.get(turn.id) ?? (this.requestedTurns.has(turn.id) ? "한국어 번역 중…" : undefined);
  }

  async translateTurn(turnId: string, text: string): Promise<void> {
    const signal = this.requests.signal;
    this.requestedTurns.add(turnId);
    this.failedTurns.delete(turnId);
    try {
      const korean = await this.translate("question", text, signal);
      if (!signal.aborted) { this.meetingKo.set(turnId, korean); this.deps.changed(); }
    } catch (error) {
      if (signal.aborted) return;
      this.meetingKo.set(turnId, failureLabel(error)); this.failedTurns.set(turnId, text); this.deps.changed();
    }
  }

  async translateReply(reply: LiveReply, retry = false): Promise<void> {
    const signal = this.requests.signal, values = answers(reply), state = this.deps.state;
    const result = values.map(() => "한국어 번역 중…");
    this.replyKo.set(reply.id, result); this.deps.changed();
    await Promise.all(values.map(async (english, index) => {
      try { result[index] = await this.replyLine(reply.id, index, english, retry, signal); }
      catch (error) { if (!signal.aborted) result[index] = `${failureLabel(error)} · 우클릭으로 재시도`; }
      if (!signal.aborted) this.deps.changed();
    }));
    const keep = new Set([state.current?.id, state.lastCompleted?.id, state.shown?.id, state.display?.id]);
    for (const id of this.replyKo.keys()) if (!keep.has(id)) this.replyKo.delete(id);
    for (const id of this.pushedAtStart) if (!keep.has(id)) this.pushedAtStart.delete(id);
    this.pushes.prune(keep);
  }

  /** Translates again what failed: the reply on screen and, with captions, the heard turns. */
  retry(): void {
    if (!this.deps.korean()) return;
    const display = this.deps.state.display;
    if (translatable(display)) void this.translateReply(display, true);
    if (this.deps.captions()) for (const [turnId, text] of this.failedTurns) void this.translateTurn(turnId, text);
  }

  /** Turning Korean (or captions) on translates what is already on screen, so the meanings do not wait for the next reply. */
  onScreen(): void {
    const state = this.deps.state, display = state.display;
    if (translatable(display) && !this.replyKo.has(display.id)) void this.translateReply(display, true);
    if (!this.deps.captions()) return;
    for (const turn of state.turns.filter(value => value.final).slice(-recentTranslatedTurns))
      if (!this.requestedTurns.has(turn.id)) void this.translateTurn(turn.id, turn.text);
  }

  /** The server's pushed line when it has one in time; otherwise (or on a retry) the client translates the line itself. */
  private async replyLine(responseId: string, index: number, english: string, retry: boolean, signal: AbortSignal): Promise<string> {
    const wait = !retry && this.pushedAtStart.has(responseId) ? pushedKoreanWaitMs : 0;
    const pushed = await this.pushes.wait(responseId, index, wait, signal);
    return pushed ?? this.translate("reply", english, signal);
  }

  private async translate(kind: CoachKind, text: string, signal: AbortSignal): Promise<string> {
    const parts: string[] = [];
    for (const chunk of translationChunks(text)) {
      for (let tries = 1; ; tries++) {
        try { parts.push(await this.deps.coach.translate(kind, chunk, signal)); break; }
        catch (error) {
          if (tries >= 3 || signal.aborted || !retryable(error)) throw error;
          await new Promise(resolve => setTimeout(resolve, 300 * tries));
        }
      }
    }
    return parts.join(" ");
  }
}
