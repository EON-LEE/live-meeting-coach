import { FeedbackMarks, isFeedbackReason, type FeedbackReason } from "./feedback.js";
import { write } from "./live-dom.js";

export interface FeedbackDeps {
  /** The `#feedback` row: chips with `data-reason`, the toggle with `aria-expanded`, `.live-feedback-reasons` and `.live-feedback-note`. */
  root: HTMLElement;
  /** Sends the reason for the reply; false when it could not be sent. */
  send(responseId: string, reason: FeedbackReason): boolean;
}
export interface FeedbackRow {
  /** The reply on screen, and whether feedback can be sent now; without either the row is hidden. */
  render(responseId: string | undefined, available: boolean): void;
}

export const feedbackNote = "보냈어요 · 고마워요";

/**
 * One-tap feedback under the answers: "좋아요", or "아쉬워요" and then why. Each reason is sent once per reply and stays
 * pressed for it; a different reply on screen starts closed and unpressed. Nothing here moves focus.
 */
export function createFeedback({ root, send }: FeedbackDeps): FeedbackRow {
  const marks = new FeedbackMarks();
  const more = root.querySelector<HTMLButtonElement>("button[aria-expanded]")!;
  const reasons = root.querySelector<HTMLElement>(".live-feedback-reasons")!;
  const note = root.querySelector<HTMLElement>(".live-feedback-note")!;
  const chips = [...root.querySelectorAll<HTMLButtonElement>("button[data-reason]")];
  let reply: string | undefined;

  function expand(open: boolean): void {
    if (more.getAttribute("aria-expanded") !== String(open)) more.setAttribute("aria-expanded", String(open));
    if (reasons.hidden === open) reasons.hidden = !open;
  }
  function press(): void {
    for (const chip of chips) {
      const reason = chip.dataset.reason, pressed = String(!!reply && isFeedbackReason(reason) && marks.has(reply, reason));
      if (chip.getAttribute("aria-pressed") !== pressed) chip.setAttribute("aria-pressed", pressed);
    }
  }

  root.addEventListener("click", event => {
    const button = (event.target as Element | null)?.closest?.("button");
    if (!button || !root.contains(button)) return;
    if (button === more) { expand(more.getAttribute("aria-expanded") !== "true"); return; }
    const reason = button.dataset.reason;
    if (!reply || !isFeedbackReason(reason) || marks.has(reply, reason) || !send(reply, reason)) return;
    marks.mark(reply, reason);
    press();
    write(note, feedbackNote);
  });

  return {
    render(responseId, available) {
      const hidden = !responseId || !available;
      if (root.hidden !== hidden) root.hidden = hidden;
      if (responseId === reply) return;
      reply = responseId;
      expand(false);
      write(note, "");
      press();
    },
  };
}
