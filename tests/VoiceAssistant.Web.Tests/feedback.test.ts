import { test } from "node:test";
import assert from "node:assert/strict";
import { FeedbackMarks, feedbackReasons, isFeedbackReason, validateFeedback } from "../../src/VoiceAssistant.Web/src/feedback.js";

test("Feedback: one reply id and one of the seven reasons, never free text", () => {
  assert.deepEqual([...feedbackReasons], ["good", "late", "wrong", "thin", "hard_to_read", "unnecessary", "missing"]);
  assert.deepEqual(validateFeedback("r1", "late"), { type: "response.feedback", responseId: "r1", reason: "late" });
  for (const reason of ["", "Late", "slow", "too late", 1, null, undefined])
    assert.throws(() => validateFeedback("r1", reason), /reason/, JSON.stringify(reason));
  for (const id of ["", 1, null, undefined, "x".repeat(129)])
    assert.throws(() => validateFeedback(id, "good"), /responseId/, JSON.stringify(id));
  assert.equal(isFeedbackReason("hard_to_read"), true);
  assert.equal(isFeedbackReason("toString"), false);
});

test("Feedback: each reason is sent once per reply, and only recent replies are remembered", () => {
  const marks = new FeedbackMarks(2);
  assert.equal(marks.mark("r1", "good"), true);
  assert.equal(marks.mark("r1", "good"), false, "already sent");
  assert.equal(marks.mark("r1", "late"), true, "another reason for the same reply");
  assert.equal(marks.has("r1", "late"), true);
  assert.equal(marks.has("r2", "late"), false);
  marks.mark("r2", "wrong");
  marks.mark("r3", "thin");
  assert.equal(marks.has("r1", "good"), false, "the oldest reply is forgotten");
  assert.equal(marks.has("r2", "wrong"), true);
  assert.equal(marks.has("r3", "thin"), true);
});
