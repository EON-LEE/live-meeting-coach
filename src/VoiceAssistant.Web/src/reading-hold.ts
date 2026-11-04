/** What the reading hold needs to know of a reply. */
export interface HoldableReply {
  readonly id: string;
  readonly complete: boolean;
  /** The primary line is final, so it can be read before the reply completes. */
  readonly primaryDone?: boolean;
}

/**
 * Which reply the reading view shows: the newest readable one, or, while the user holds it, the one held. A held reply keeps its
 * text on screen while newer replies arrive, and only gains what arrives later for itself (alternative, grounding, sources).
 */
export class ReadingHold<R extends HoldableReply> {
  private held: R | null = null;
  private latest: R | null = null;

  /** The newest readable reply. */
  get shown(): R | null { return this.latest; }
  /** The reply on screen. */
  get display(): R | null { return this.held ?? this.latest; }
  get isHeld(): boolean { return this.held !== null; }
  /** A newer readable reply waits behind the held one. */
  get hasNewer(): boolean { return !!this.held && !!this.latest && this.held.id !== this.latest.id; }

  /** Holds the reply on screen, once it can be read: a draft is never held. */
  hold(): void {
    const display = this.display;
    if (display && (display.complete || display.primaryDone)) this.held = display;
  }
  release(): void { this.held = null; }
  /** A reply became readable, or a readable one gained more. */
  show(reply: R): void {
    this.latest = reply;
    if (this.held?.id === reply.id) this.held = reply;
  }
  /** The reply must leave the screen, whether it is shown or held. */
  withdraw(id: string): void {
    if (this.latest?.id === id) this.latest = null;
    if (this.held?.id === id) this.held = null;
  }
  reset(): void { this.held = null; this.latest = null; }
}
