import type { Grounding } from "./protocol.js";

/**
 * The words of the reading view about the answer on screen: the status line under the title, the hold button and its menu
 * item, and the grounding badge and details. Pure, so every branch is pinned by a table test; the page only turns the live
 * state into a {@link ReadingInput}.
 */
export interface ReadingInput {
  /** A reply is held on screen (`LiveState.readingHeld`). */
  held: boolean;
  /** A newer reply waits behind the hold (`LiveState.hasNewerReply`). */
  hasNewer: boolean;
  /** Connected, and a newer reply is being produced. */
  generating: boolean;
  /** The reply on screen is the one being produced: its alternative and grounding are still coming. */
  finishing: boolean;
  /** A reply is on screen. */
  display: boolean;
  /** The reply on screen stopped after its primary line. */
  interrupted: boolean;
  /** A live session runs (starting, connected or reconnecting). */
  active: boolean;
  /** What the status says when nothing runs and nothing is on screen (`idleStatus`). */
  idle: string;
}

export function readingStatus(input: ReadingInput): string {
  if (input.held) {
    return input.hasNewer ? "새 답변 도착 · 고정을 해제하면 표시합니다"
      : input.generating ? "화면 고정됨 · 새 답변 준비 중"
        : "화면 고정됨 · 오디오 전송 상태는 위에서 확인하세요";
  }
  if (input.finishing) return "추천 문장입니다 · 대안과 근거를 확인하는 중";
  if (input.generating) return input.display ? "새 답변 준비 중 · 아래는 이전 질문용 답변" : "답변 준비 중 · 완성되면 표시합니다";
  if (input.interrupted) return "답변 생성이 중단되었습니다 · 근거를 확인하지 못했습니다";
  if (input.display) return "추천 문장입니다 · 내용 확인 후 사용하세요";
  return input.active ? "듣는 중 · 답변이 완성되면 표시합니다" : input.idle;
}

/** The hold button under the title: it holds the answer on screen, releases it, or shows the newer reply waiting behind it. */
export function holdButton(input: Pick<ReadingInput, "held" | "hasNewer" | "display">): { label: string; pressed: boolean; disabled: boolean } {
  return { label: input.held ? input.hasNewer ? "새 답변 보기" : "고정 해제" : "답변 고정", pressed: input.held, disabled: !input.display };
}

/** The same action in the right-click menu, which says that holding the screen keeps listening. */
export function holdMenuLabel(input: Pick<ReadingInput, "held" | "hasNewer">): string {
  return input.held ? input.hasNewer ? "새 답변 보기 / 화면 고정 해제" : "화면 고정 해제" : "읽는 동안 답변 고정 (오디오는 계속 듣기)";
}

const groundingBadges: Readonly<Record<Grounding, string>> = {
  grounded: "문서 인용 있음 · 내용 확인 필요", no_matches: "문서 근거 없음 · 사실 확인 필요",
  unavailable: "자료 확인 불가 · 사실 답변에 의존하지 마세요", disabled: "문서 검색 미사용 · 대화 맥락만 사용",
};
const groundingDetails: Readonly<Record<Grounding, string>> = {
  grounded: "근거 있음 · 내 업로드 문서", no_matches: "문서 근거 없음 · 사실 확인 필요",
  unavailable: "자료 확인 불가 · 문서 검색이 실패해 일반적인 답변만 보여 줍니다", disabled: "이번 답변은 문서 검색을 사용하지 않음",
};

/** The badge under the answers: the grounding of the reply on screen, or why there is none yet. */
export function groundingBadge(grounding: Grounding | undefined, input: Pick<ReadingInput, "interrupted" | "generating">): string {
  return grounding ? groundingBadges[grounding]
    : input.interrupted ? "답변 중단 · 근거 확인 안 됨" : input.generating ? "근거 확인 중" : "근거 상태 대기";
}

/** The grounding line in the details. */
export function groundingDetail(grounding: Grounding | undefined): string {
  return grounding ? groundingDetails[grounding] : "문서 근거 상태는 답변 완료 후 표시됩니다";
}
