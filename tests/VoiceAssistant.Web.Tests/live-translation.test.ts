import { test } from "node:test";
import assert from "node:assert/strict";
import { CoachError, type CoachKind } from "../../src/VoiceAssistant.Web/src/coach-client.js";
import { LiveState } from "../../src/VoiceAssistant.Web/src/live-state.js";
import { LiveTranslations } from "../../src/VoiceAssistant.Web/src/live-translation.js";

interface Call { kind: CoachKind; text: string; resolve(korean: string): void; reject(error: unknown): void }
function setup(options: { korean?: boolean; captions?: boolean } = {}) {
  const calls: Call[] = [];
  let changes = 0;
  const state = new LiveState();
  const translations = new LiveTranslations({
    coach: { translate: (kind, text) => new Promise<string>((resolve, reject) => { calls.push({ kind, text, resolve, reject }); }) },
    state, korean: () => options.korean ?? true, captions: () => options.captions ?? true,
    acceptsPushed: korean => /[\uac00-\ud7a3]/u.test(korean), changed: () => { changes++; },
  });
  return { calls, state, translations, changes: () => changes };
}
const settle = (): Promise<void> => new Promise(resolve => setImmediate(resolve));
function heard(state: LiveState, id: string, text: string, final = true): void {
  state.apply({ type: final ? "transcript.final" : "transcript.partial", turnId: id, revision: 1, text });
}
function completed(state: LiveState, id: string, turnId: string, suggestions: string[]): void {
  state.apply({ type: "response.started", turnId, responseId: id });
  state.apply({ type: "response.completed", turnId, responseId: id, text: suggestions[0]!, suggestions, sources: [] });
}

test("A heard turn shows that it is being translated, then its Korean; a failure is labeled and can be retried", async () => {
  const { calls, state, translations } = setup();
  heard(state, "t1", "When can we ship?");
  heard(state, "t2", "Still talking", false);
  const turn = state.turns[0]!;
  assert.equal(translations.turnKorean(turn), undefined, "never requested");
  assert.equal(translations.turnKorean(state.turns[1]!), "듣는 중…");
  const first = translations.translateTurn("t1", turn.text);
  assert.equal(translations.turnKorean(turn), "한국어 번역 중…");
  calls[0]!.reject(new CoachError("invalid_request"));
  await first;
  assert.equal(translations.turnKorean(turn), "한국어 번역 실패");
  assert.equal(translations.hasFailedTurns, true);
  translations.retry();
  assert.equal(calls.length, 2);
  assert.deepEqual([calls[1]!.kind, calls[1]!.text], ["question", "When can we ship?"]);
  calls[1]!.resolve("언제 출시할 수 있나요?");
  await settle();
  assert.equal(translations.turnKorean(turn), "언제 출시할 수 있나요?");
  assert.equal(translations.hasFailedTurns, false);
});

test("With Korean off a heard turn has no Korean line and nothing is retried", () => {
  const { calls, state, translations } = setup({ korean: false });
  heard(state, "t1", "Hello");
  assert.equal(translations.turnKorean(state.turns[0]!), undefined);
  translations.retry();
  assert.equal(calls.length, 0);
});

test("A reply waits for the line the server pushes only when it began while the server pushed Korean", async () => {
  const { calls, state, translations } = setup();
  heard(state, "t1", "Next step?");
  completed(state, "r1", "t1", ["Check the plan.", "Could we check it together?"]);
  translations.started("r1");
  translations.record({ type: "response.korean", turnId: "t1", responseId: "r1", index: 0, korean: "계획을 확인하세요." });
  translations.record({ type: "response.korean", turnId: "t1", responseId: "r1", index: 1, failed: true });
  const done = translations.translateReply(state.display!);
  assert.deepEqual(translations.replyLines("r1"), ["한국어 번역 중…", "한국어 번역 중…"]);
  await settle();
  // The pushed primary line is used as it is; the failed alternative falls back to a translation request.
  assert.deepEqual(calls.map(call => [call.kind, call.text]), [["reply", "Could we check it together?"]]);
  calls[0]!.resolve("함께 확인해 볼까요?");
  await done;
  assert.deepEqual(translations.replyLines("r1"), ["계획을 확인하세요.", "함께 확인해 볼까요?"]);
  assert.equal(translations.pushed("r1", 0), "계획을 확인하세요.");
  assert.equal(translations.pushed("r1", 1), null);
});

test("Clearing ends running work: a translation that finishes afterwards writes nothing", async () => {
  const { calls, state, translations, changes } = setup();
  heard(state, "t1", "Hello there");
  completed(state, "r1", "t1", ["Hi."]);
  const turn = translations.translateTurn("t1", "Hello there"), reply = translations.translateReply(state.display!);
  await settle();
  assert.equal(calls.length, 2);
  translations.clear();
  const before = changes();
  calls[0]!.resolve("안녕하세요"); calls[1]!.resolve("안녕.");
  await Promise.all([turn, reply]);
  assert.equal(translations.turnKorean(state.turns[0]!), undefined);
  assert.deepEqual(translations.replyLines("r1"), []);
  assert.equal(changes(), before, "nothing changed on screen");
});

test("Switching Korean on translates the reply on screen and the latest heard turns only once", async () => {
  const { calls, state, translations } = setup();
  for (let i = 1; i <= 8; i++) heard(state, `t${i}`, `Line ${i}`);
  completed(state, "r1", "t8", ["Sure."]);
  translations.onScreen();
  await settle();
  assert.deepEqual(calls.map(call => call.text).sort(), ["Line 3", "Line 4", "Line 5", "Line 6", "Line 7", "Line 8", "Sure."]);
  translations.onScreen();
  await settle();
  assert.equal(calls.length, 7, "requested turns and a reply with Korean are not requested again");
});
