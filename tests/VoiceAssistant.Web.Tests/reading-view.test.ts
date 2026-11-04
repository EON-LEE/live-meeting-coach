import { test } from "node:test";
import assert from "node:assert/strict";
import { groundingBadge, groundingDetail, holdButton, holdMenuLabel, readingStatus,
  type ReadingInput } from "../../src/VoiceAssistant.Web/src/reading-view.js";

const idle = "시작을 누르면 듣기 시작 · 설정은 우클릭 메뉴";
const input = (extra: Partial<ReadingInput>): ReadingInput => ({ held: false, hasNewer: false, generating: false, finishing: false,
  display: false, interrupted: false, active: false, idle, ...extra });

test("Reading view: the status line says what the answer on screen is, most specific first", () => {
  const cases: [Partial<ReadingInput>, string][] = [
    [{ held: true, display: true, hasNewer: true, generating: true }, "새 답변 도착 · 고정을 해제하면 표시합니다"],
    [{ held: true, display: true, generating: true }, "화면 고정됨 · 새 답변 준비 중"],
    [{ held: true, display: true, active: true }, "화면 고정됨 · 오디오 전송 상태는 위에서 확인하세요"],
    [{ display: true, generating: true, finishing: true }, "추천 문장입니다 · 대안과 근거를 확인하는 중"],
    [{ display: true, generating: true }, "새 답변 준비 중 · 아래는 이전 질문용 답변"],
    [{ generating: true, active: true }, "답변 준비 중 · 완성되면 표시합니다"],
    [{ display: true, interrupted: true, active: true }, "답변 생성이 중단되었습니다 · 근거를 확인하지 못했습니다"],
    [{ display: true, active: true }, "추천 문장입니다 · 내용 확인 후 사용하세요"],
    [{ active: true }, "듣는 중 · 답변이 완성되면 표시합니다"],
    [{}, idle],
  ];
  for (const [extra, expected] of cases) assert.equal(readingStatus(input(extra)), expected, JSON.stringify(extra));
});

test("Reading view: the hold button and its menu item hold, release, or show the newer reply", () => {
  assert.deepEqual(holdButton(input({})), { label: "답변 고정", pressed: false, disabled: true }, "nothing on screen to hold");
  assert.deepEqual(holdButton(input({ display: true })), { label: "답변 고정", pressed: false, disabled: false });
  assert.deepEqual(holdButton(input({ display: true, held: true })), { label: "고정 해제", pressed: true, disabled: false });
  assert.deepEqual(holdButton(input({ display: true, held: true, hasNewer: true })), { label: "새 답변 보기", pressed: true, disabled: false });
  assert.equal(holdMenuLabel(input({})), "읽는 동안 답변 고정 (오디오는 계속 듣기)");
  assert.equal(holdMenuLabel(input({ held: true })), "화면 고정 해제");
  assert.equal(holdMenuLabel(input({ held: true, hasNewer: true })), "새 답변 보기 / 화면 고정 해제");
});

test("Reading view: the grounding badge and details name the grounding, or why it is not known yet", () => {
  assert.equal(groundingBadge("grounded", input({ generating: true })), "문서 인용 있음 · 내용 확인 필요", "a known grounding wins");
  assert.equal(groundingBadge("no_matches", input({})), "문서 근거 없음 · 사실 확인 필요");
  assert.equal(groundingBadge("unavailable", input({})), "자료 확인 불가 · 사실 답변에 의존하지 마세요");
  assert.equal(groundingBadge("disabled", input({})), "문서 검색 미사용 · 대화 맥락만 사용");
  assert.equal(groundingBadge(undefined, input({ interrupted: true, generating: true })), "답변 중단 · 근거 확인 안 됨");
  assert.equal(groundingBadge(undefined, input({ generating: true })), "근거 확인 중");
  assert.equal(groundingBadge(undefined, input({})), "근거 상태 대기");
  assert.equal(groundingDetail("grounded"), "근거 있음 · 내 업로드 문서");
  assert.equal(groundingDetail("no_matches"), "문서 근거 없음 · 사실 확인 필요");
  assert.equal(groundingDetail("unavailable"), "자료 확인 불가 · 문서 검색이 실패해 일반적인 답변만 보여 줍니다");
  assert.equal(groundingDetail("disabled"), "이번 답변은 문서 검색을 사용하지 않음");
  assert.equal(groundingDetail(undefined), "문서 근거 상태는 답변 완료 후 표시됩니다");
});
