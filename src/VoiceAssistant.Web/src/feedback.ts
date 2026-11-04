/**
 * Content-free signals about replies, sent only to a server that announced them in `session.ready`: one-tap feedback
 * (`response.feedback`). Ids and codes only, never text.
 */

/** Why a reply helped ("good") or did not; "missing" means the answer the user needed was not shown. */
export const feedbackReasons = ["good", "late", "wrong", "thin", "hard_to_read", "unnecessary", "missing"] as const;
export type FeedbackReason = typeof feedbackReasons[number];

export function isFeedbackReason(value: unknown): value is FeedbackReason {
  return (feedbackReasons as readonly unknown[]).includes(value);
}

function responseId(value: unknown): string {
  if (typeof value !== "string" || !value || value.length > 128) throw new Error("A responseId must be a non-empty string.");
  return value;
}

/** Validate a `response.feedback` command: a reply id and one known reason. */
export function validateFeedback(id: unknown, reason: unknown): { type: "response.feedback"; responseId: string; reason: FeedbackReason } {
  if (!isFeedbackReason(reason)) throw new Error(`Feedback reason must be one of ${feedbackReasons.join(", ")}.`);
  return { type: "response.feedback", responseId: responseId(id), reason };
}

/** Which reasons were already sent for which reply, so each is sent once and its chip shows it; only recent replies are kept. */
export class FeedbackMarks {
  private readonly sent = new Map<string, Set<FeedbackReason>>();
  constructor(private readonly replies = 20) {}

  has(id: string, reason: FeedbackReason): boolean { return !!this.sent.get(id)?.has(reason); }
  /** Records the reason for the reply; false when it was already sent, so there is nothing to send. */
  mark(id: string, reason: FeedbackReason): boolean {
    const reasons = this.sent.get(id) ?? new Set<FeedbackReason>();
    if (reasons.has(reason)) return false;
    reasons.add(reason);
    this.sent.delete(id); this.sent.set(id, reasons);
    for (const oldest of this.sent.keys()) { if (this.sent.size <= this.replies) break; this.sent.delete(oldest); }
    return true;
  }
}
