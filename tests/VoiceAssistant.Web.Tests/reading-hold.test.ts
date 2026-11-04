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

test("Reading hold: the page releases only its own hold, and its hold never outlives the newer reply it waited for", () => {
  const page = new ReadingHold<Reply>();
  page.show(reply("r1"));
  page.hold("listen");
  assert.equal(page.auto, true);
  page.hold();
  assert.equal(page.auto, true, "an existing hold stays as it is");
  page.show(reply("r2"));
  page.releaseAuto();
  assert.deepEqual([page.display?.id, page.isHeld], ["r2", false]);

  const user = new ReadingHold<Reply>();
  user.show(reply("r1"));
  user.hold();
  user.hold("listen");
  user.show(reply("r2"));
  user.releaseAuto();
  user.restart();
  assert.deepEqual([user.display?.id, user.isHeld, user.auto], ["r1", true, false], "the user's hold stays");

  const withdrawn = new ReadingHold<Reply>();
  withdrawn.show(reply("r1"));
  withdrawn.hold("listen");
  withdrawn.show(reply("r2"));
  withdrawn.withdraw("r2");
  assert.deepEqual([withdrawn.display?.id, withdrawn.shown?.id, withdrawn.isHeld], ["r1", "r1", false], "the held answer simply stays");

  const restarted = new ReadingHold<Reply>();
  restarted.show(reply("r1"));
  restarted.hold("listen");
  restarted.show(reply("r2"));
  restarted.restart();
  assert.deepEqual([restarted.display?.id, restarted.isHeld], ["r2", false]);
});
