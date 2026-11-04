import { test } from "node:test";
import assert from "node:assert/strict";
import { DwellTracker } from "../../src/VoiceAssistant.Web/src/live-dwell.js";

function tracker(interval = 5000, keep = 100) {
  const clock = { at: 0 };
  return { clock, dwell: new DwellTracker(() => clock.at, interval, keep) };
}

test("Dwell: each reply's on-screen time, sent at most every interval, and at once when asked", () => {
  const { clock, dwell } = tracker();
  dwell.issued("r1"); dwell.issued("r2");
  clock.at = 100; dwell.show("r1");
  clock.at = 1300.4; dwell.show("r2");
  assert.deepEqual(dwell.take(), [], "the first batch waits for the interval");
  clock.at = 5000; dwell.show("r2");
  assert.deepEqual(dwell.take(), [[{ responseId: "r1", visibleMs: 1200 }]], "whole milliseconds; the reply on screen is not counted yet");
  clock.at = 5100; dwell.show("r1");
  assert.deepEqual(dwell.take(), [], "the next batch waits for the interval again");
  clock.at = 6100; dwell.show(undefined);
  assert.deepEqual(dwell.take(true), [[{ responseId: "r2", visibleMs: 3800 }, { responseId: "r1", visibleMs: 1000 }]]);
  assert.deepEqual(dwell.take(true), [], "nothing is sent twice");
});

test("Dwell: hiding and showing the same reply again adds up, and a hidden page counts nothing", () => {
  const { clock, dwell } = tracker();
  dwell.issued("r1");
  dwell.show("r1");
  clock.at = 2000; dwell.show(undefined);
  clock.at = 60_000; dwell.show("r1");
  clock.at = 61_500; dwell.show(undefined);
  assert.deepEqual(dwell.take(true), [[{ responseId: "r1", visibleMs: 3500 }]]);
});

test("Dwell: only replies this connection issued, nothing after the connection is dropped, and no empty times", () => {
  const { clock, dwell } = tracker();
  dwell.show("r9");
  clock.at = 1000; dwell.show(undefined);
  assert.deepEqual(dwell.take(true), [], "an id from another connection");
  dwell.issued("r1");
  dwell.show("r1"); dwell.show(undefined);
  assert.deepEqual(dwell.take(true), [], "zero milliseconds is not a time on screen");
  dwell.show("r1");
  clock.at = 3000; dwell.drop();
  dwell.show("r1");
  clock.at = 4000; dwell.show(undefined);
  assert.deepEqual(dwell.take(true), [], "the dropped connection's ids and times are gone");
});

test("Dwell: batches of at most 20, at most an hour per reply, and only the newest replies when too many wait", () => {
  const { clock, dwell } = tracker(5000, 30);
  for (let i = 1; i <= 45; i++) { dwell.issued(`r${i}`); dwell.show(`r${i}`); clock.at += 10; }
  dwell.show(undefined);
  const batches = dwell.take(true);
  assert.deepEqual(batches.map(batch => batch.length), [20, 10]);
  assert.equal(batches[0]![0]!.responseId, "r16", "the oldest that did not fit were dropped");
  assert.ok(batches.flat().every(item => item.visibleMs === 10));
  const late = tracker();
  late.dwell.issued("r1"); late.dwell.show("r1");
  late.clock.at = 2 * 3_600_000; late.dwell.show(undefined);
  assert.deepEqual(late.dwell.take(true), [[{ responseId: "r1", visibleMs: 3_600_000 }]]);
});
