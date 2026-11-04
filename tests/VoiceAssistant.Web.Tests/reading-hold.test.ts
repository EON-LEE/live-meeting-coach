import { test } from "node:test";
import assert from "node:assert/strict";
import { ReadingHold, type HoldableReply } from "../../src/VoiceAssistant.Web/src/reading-hold.js";

interface Reply extends HoldableReply { text: string }
const reply = (id: string, text = id, complete = true): Reply => ({ id, text, complete });

test("Reading hold: a draft is never held; a readable reply is, and newer replies wait behind it", () => {
  const hold = new ReadingHold<Reply>();
  hold.hold();
  assert.equal(hold.isHeld, false, "nothing on screen");
  hold.show({ id: "r1", text: "draft", complete: false });
  hold.hold();
  assert.equal(hold.isHeld, false, "an unfinished line without primaryDone");
  hold.show({ id: "r1", text: "Line one.", complete: false, primaryDone: true });
  hold.hold();
  assert.equal(hold.isHeld, true);
  hold.show(reply("r2"));
  assert.equal(hold.display?.id, "r1");
  assert.equal(hold.shown?.id, "r2");
  assert.equal(hold.hasNewer, true);
  hold.release();
  assert.equal(hold.display?.id, "r2");
  assert.equal(hold.hasNewer, false);
});

test("Reading hold: the held reply gains what arrives for it later, and a withdrawn reply leaves wherever it is", () => {
  const hold = new ReadingHold<Reply>();
  hold.show({ id: "r1", text: "Line one.", complete: false, primaryDone: true });
  hold.hold();
  hold.show(reply("r1", "Line one. More."));
  assert.equal(hold.display?.text, "Line one. More.");
  hold.show(reply("r2"));
  hold.withdraw("r2");
  assert.equal(hold.shown, null);
  assert.equal(hold.display?.id, "r1", "the held reply stays");
  hold.withdraw("r1");
  assert.equal(hold.display, null);
  assert.equal(hold.isHeld, false);
  hold.show(reply("r3")); hold.hold(); hold.reset();
  assert.deepEqual([hold.display, hold.isHeld], [null, false]);
});
