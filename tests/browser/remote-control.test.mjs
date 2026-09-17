// Checks that the remote control viewer (src/Fleeto.Web/wwwroot/js/remote-control-ui.mjs) parses a FrameUpdate the Go endpoint produced
// (agent/internal/screen/tiles.go) into the right tile rectangles, draws each one at its place, and acknowledges the frame once, only
// after the last update. Run: node --test tests/browser/remote-control.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";

// A real update built by screen.Updates(9, [{64,128,64,32,PNG,"PNGDATA"},{0,0,128,64,JPEG,"JPG"}]) (one update, last).
const UPDATE_B64 = "GAAAAAkBAAIAQACAAEAAIAEAAAAHUE5HREFUQQAAAAAAgABAAgAAAANKUEc=";

function installDom() {
  const drawn = [];
  const canvas = {
    width: 0, height: 0, classList: { toggle() {} }, tabIndex: 0,
    getContext: () => ({ drawImage: (bitmap, x, y, w, h) => drawn.push({ x, y, w, h, type: bitmap.type }) }),
    addEventListener() {}, removeEventListener() {}, getBoundingClientRect: () => ({ left: 0, top: 0, width: 100, height: 100 }), focus() {}
  };
  globalThis.document = {
    createElement: (tag) => tag === "canvas" ? canvas : { className: "", style: {}, append() {}, appendChild() {}, addEventListener() {}, replaceChildren() {} }
  };
  globalThis.performance = { now: () => 0 };
  const bitmaps = [];
  globalThis.createImageBitmap = async (blob) => {
    const b = { type: blob.type, close() {} };
    bitmaps.push(b);
    return b;
  };
  return { drawn };
}

test("an update draws its tiles at their places and acknowledges the frame once", async () => {
  const { drawn } = installDom();
  const { Viewer } = await import("../../src/Fleeto.Web/wwwroot/js/remote-control-ui.mjs");
  const acks = [];
  const viewer = new Viewer({ activity() {}, sendControl() {} }, { replaceChildren() {}, append() {} }, {});
  viewer.render();

  const body = Uint8Array.from(Buffer.from(UPDATE_B64, "base64")).subarray(1); // the session strips the type byte before onUpdate
  await viewer.onUpdate(body, (frame) => acks.push(frame));

  assert.deepEqual(drawn, [
    { x: 64, y: 128, w: 64, h: 32, type: "image/png" },
    { x: 0, y: 0, w: 128, h: 64, type: "image/jpeg" }
  ]);
  assert.deepEqual(acks, [9], "the frame is acknowledged once, after the last update");
});

test("a truncated update draws what it can and does not throw", async () => {
  const { drawn } = installDom();
  const { Viewer } = await import("../../src/Fleeto.Web/wwwroot/js/remote-control-ui.mjs");
  const viewer = new Viewer({ activity() {}, sendControl() {} }, { replaceChildren() {}, append() {} }, {});
  viewer.render();
  // Cut a tile's data off in the middle: the loop stops at the incomplete tile instead of reading past the end.
  const body = Uint8Array.from(Buffer.from(UPDATE_B64, "base64")).subarray(1).slice(0, 24);
  await viewer.onUpdate(body, () => {});
  assert.ok(drawn.length <= 1, "only the tiles that were complete are drawn");
});

test("an empty last update (no tiles) still acknowledges the frame", async () => {
  installDom();
  const { Viewer } = await import("../../src/Fleeto.Web/wwwroot/js/remote-control-ui.mjs");
  const viewer = new Viewer({ activity() {}, sendControl() {} }, { replaceChildren() {}, append() {} }, {});
  viewer.render();
  // frame 3, flags last, count 0
  const body = new Uint8Array([0, 0, 0, 3, 1, 0, 0]);
  const acks = [];
  await viewer.onUpdate(body, (f) => acks.push(f));
  assert.deepEqual(acks, [3]);
});
