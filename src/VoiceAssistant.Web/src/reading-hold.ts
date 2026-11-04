/** What the reading hold needs to know of a reply. */
export interface HoldableReply {
  readonly id: string;
  readonly complete: boolean;
  /** The primary line is final, so it can be read before the reply completes. */
  readonly primaryDone?: boolean;
}

/**
 * Who holds the reply on screen: the user ("user"), or the page while the other person is still talking and the server says the
 * moment is for listening ("listen"). The page releases only its own hold, never the user's.
 */
export type HoldReason = "user" | "listen";

/**
 * Which reply the reading view shows: the newest readable one, or, while it is held, the one held. A held reply keeps its text on
 * screen while newer replies arrive, and only gains what arrives later for itself (alternative, grounding, sources). Nothing is
 * ever dropped by a hold: the newest reply always waits behind it, one tap away.
 */
export class ReadingHold<R extends HoldableReply> {
  private held: R | null = null;
  private latest: R | null = null;
  private reason: HoldReason | null = null;

  /** The newest readable reply. */
  get shown(): R | null { return this.latest; }
  /** The reply on screen. */
  get display(): R | null { return this.held ?? this.latest; }
  get isHeld(): boolean { return this.held !== null; }
  /** The page holds the reply on screen while the other person talks, not the user. */
  get auto(): boolean { return this.held !== null && this.reason === "listen"; }
  /** A newer readable reply waits behind the held one. */
  get hasNewer(): boolean { return !!this.held && !!this.latest && this.held.id !== this.latest.id; }

  /** Holds the reply on screen, once it can be read: a draft is never held, and an existing hold stays as it is. */
  hold(reason: HoldReason = "user"): void {
    const display = this.display;
    if (this.held || !display || !(display.complete || display.primaryDone)) return;
    this.held = display; this.reason = reason;
  }
  release(): void { this.held = null; this.reason = null; }
  /** Ends the page's own hold; the user's stays. */
  releaseAuto(): void { if (this.auto) this.release(); }
  /** A reply became readable, or a readable one gained more. */
  show(reply: R): void {
    this.latest = reply;
    if (this.held?.id === reply.id) this.held = reply;
  }
  /** The reply must leave the screen, whether it is shown or held. */
  withdraw(id: string): void {
    if (this.latest?.id === id) this.latest = null;
    if (this.held?.id === id) this.release();
    // The page's hold only keeps an answer while a newer one waits; without one, the held answer simply stays on screen.
    if (this.auto && !this.hasNewer) { this.latest = this.held; this.release(); }
  }
  /** A new live session continues the conversation: the user's hold stays, the page's ends with the talk it waited for. */
  restart(): void { this.releaseAuto(); }
  reset(): void { this.release(); this.latest = null; }
}
