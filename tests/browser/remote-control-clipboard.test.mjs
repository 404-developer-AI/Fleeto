// Checks the clipboard, the other technicians and the consent prompt of the remote control window (0.3.0 step 4) in
// src/Fleeto.Web/wwwroot/js/remote-control.js and remote-control-ui.mjs: the paste shortcut reaches the endpoint only after the clipboard
// text, pasted files go to the endpoint clipboard instead, text copied on the endpoint lands on this computer's clipboard and is never sent
// back, the pointers of other technicians follow them and leave with them, and copied files are offered for download.
// Run: node --test tests/browser/remote-control-clipboard.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";

class FakeElement {
  constructor(tag) {
    this.tag = tag;
    this.children = [];
    this.listeners = {};
    this.style = { setProperty: (name, value) => { this.style[name] = value; } };
    this.textContent = "";
    this.className = "";
    this.disabled = false;
    this.parent = null;
    this.scrollLeft = 0;
    this.scrollTop = 0;
    const classes = new Set();
    this.classList = {
      add: (c) => classes.add(c),
      remove: (c) => classes.delete(c),
      toggle: (c, on) => (on ? classes.add(c) : classes.delete(c)),
      contains: (c) => classes.has(c)
    };
  }
  append(...nodes) { for (const n of nodes) this.appendChild(n); }
  appendChild(node) { node.parent = this; this.children.push(node); return node; }
  replaceChildren(...nodes) { this.children = []; this.append(...nodes); }
  remove() { if (this.parent) this.parent.children = this.parent.children.filter((c) => c !== this); }
  addEventListener(type, fn) { (this.listeners[type] ??= []).push(fn); }
  removeEventListener() {}
  querySelectorAll() { return this.children.filter((c) => c.tag === "button"); }
  getContext() { return { drawImage() {} }; }
  getBoundingClientRect() { return { left: 0, top: 0, width: 100, height: 100 }; }
  focus() { globalThis.document.activeElement = this; }
  click() { for (const fn of this.listeners.click ?? []) fn(); }
}

function installDom() {
  const written = [];
  globalThis.document = {
    activeElement: null,
    createElement: (tag) => new FakeElement(tag),
    createTextNode: (text) => ({ textContent: text }),
    addEventListener() {},
    removeEventListener() {}
  };
  globalThis.performance = { now: () => 0 };
  Object.defineProperty(globalThis, "navigator", {
    value: { clipboard: { writeText: async (text) => { written.push(text); } } },
    configurable: true
  });
  return { written };
}

function key(code, extra = {}) {
  let prevented = false;
  return {
    code, key: code === "KeyV" ? "v" : code, ctrlKey: false, altKey: false, shiftKey: false, metaKey: false, ...extra,
    preventDefault: () => { prevented = true; },
    get prevented() { return prevented; }
  };
}

async function newViewer(session) {
  const { Viewer } = await import("../../src/Fleeto.Web/wwwroot/js/remote-control-ui.mjs");
  const viewer = new Viewer(session, new FakeElement("div"), {});
  viewer.render();
  return viewer;
}

function fakeSession(clipboard = true) {
  const sent = [];
  return {
    sent,
    activity() {},
    clipboardEnabled: () => clipboard,
    flushRemoteClipboard() {},
    sendControl: (type, body) => sent.push({ type, body }),
    sendClipboardText: (text) => sent.push({ type: "clipboard", body: text }),
    pasteFiles: (files) => sent.push({ type: "files", body: files }),
    downloadCopied: (index, name) => sent.push({ type: "download", body: { index, name } })
  };
}

test("the paste shortcut reaches the endpoint after the clipboard text, in order", async () => {
  installDom();
  const session = fakeSession();
  const viewer = await newViewer(session);
  viewer.canvas.focus();

  const down = key("KeyV", { ctrlKey: true });
  viewer.onKey(down, true);
  assert.equal(down.prevented, false, "the browser must fire its paste event");
  assert.deepEqual(session.sent, [], "nothing goes to the endpoint before the clipboard");

  const up = key("KeyV", { ctrlKey: true });
  viewer.onKey(up, false);
  let pasteDefault = false;
  viewer.onPaste({ preventDefault: () => { pasteDefault = true; }, clipboardData: { files: [], getData: () => "P@ssw0rd é" } });

  assert.ok(pasteDefault);
  assert.deepEqual(session.sent.map((s) => s.type), ["clipboard", 0x14, 0x14]);
  assert.equal(session.sent[0].body, "P@ssw0rd é");
  assert.equal(session.sent[1].body.down, true);
  assert.equal(session.sent[2].body.down, false);
});

test("without a paste event the shortcut still reaches the endpoint", async () => {
  installDom();
  const session = fakeSession();
  const viewer = await newViewer(session);
  viewer.onKey(key("KeyV", { ctrlKey: true }), true);
  await new Promise((r) => setTimeout(r, 400));
  assert.deepEqual(session.sent.map((s) => s.type), [0x14]);
});

test("pasted files go to the endpoint clipboard and the shortcut does not", async () => {
  installDom();
  const session = fakeSession();
  const viewer = await newViewer(session);
  viewer.canvas.focus();
  viewer.onKey(key("KeyV", { ctrlKey: true }), true);
  const file = { name: "invoice.pdf", size: 10 };
  viewer.onPaste({ preventDefault() {}, clipboardData: { files: [file], getData: () => "" } });
  const up = key("KeyV", { ctrlKey: true });
  viewer.onKey(up, false);
  assert.deepEqual(session.sent.map((s) => s.type), ["files"]);
  assert.deepEqual(session.sent[0].body, [file]);
});

test("with the clipboard off the shortcut is an ordinary key", async () => {
  installDom();
  const session = fakeSession(false);
  const viewer = await newViewer(session);
  const down = key("KeyV", { ctrlKey: true });
  viewer.onKey(down, true);
  assert.equal(down.prevented, true);
  assert.deepEqual(session.sent.map((s) => s.type), [0x14]);
});

test("pointers of other technicians follow them and leave with them", async () => {
  installDom();
  const viewer = await newViewer(fakeSession());
  viewer.onInfo({ monitors: [], monitor: 0, width: 200, height: 100 });
  viewer.onParticipants([{ id: "a", name: "Anna", you: true }, { id: "b", name: "Bert", you: false }]);
  assert.equal(viewer.participantList.children.length, 2);

  viewer.onPeerPointer({ id: "a", x: 1, y: 1 }); // your own pointer is never drawn
  viewer.onPeerPointer({ id: "b", x: 100, y: 50 });
  assert.equal(viewer.peerLayer.children.length, 1);
  const marker = viewer.peerLayer.children[0];
  assert.equal(marker.children[0].textContent, "Bert");
  assert.equal(marker.style.transform, "translate(50px, 50px)");

  viewer.onParticipants([{ id: "a", name: "Anna", you: true }]);
  assert.equal(viewer.peerLayer.children.length, 0);
});

test("copied files are offered for download and folders are counted", async () => {
  installDom();
  const session = fakeSession();
  const viewer = await newViewer(session);
  viewer.onClipboardFiles({ files: [{ index: 0, name: "report.pdf", size: 2 * 1024 * 1024 }], folders: 1 });
  const buttons = viewer.copiedFiles.children.filter((c) => c.tag === "button");
  assert.equal(buttons.length, 1);
  assert.equal(buttons[0].textContent, "report.pdf (2.0 MB)");
  assert.match(viewer.copiedFiles.children[0].textContent, /1 folder cannot be downloaded/);
  buttons[0].click();
  assert.deepEqual(session.sent.at(-1), { type: "download", body: { index: 0, name: "report.pdf" } });
  viewer.onClipboardFiles({ files: [] });
  assert.equal(viewer.copiedFiles.children.length, 0);
});

test("the consent prompt counts down and the answer replaces it", async () => {
  installDom();
  const viewer = await newViewer(fakeSession());
  viewer.onConsent({ state: "waiting", secondsLeft: 30, message: "Waiting for ACME\\anna to allow the session." });
  assert.match(viewer.status.textContent, /30 s left/);
  viewer.onConsent({ state: "granted", message: "ACME\\anna allowed the session." });
  assert.equal(viewer.status.textContent, "ACME\\anna allowed the session.");
  assert.equal(viewer.consentTimer, null);
});

test("text copied on the endpoint lands on this computer's clipboard and is never sent back", async () => {
  const { written } = installDom();
  const { createSession } = await import("../../src/Fleeto.Web/wwwroot/js/remote-control.js");
  const session = createSession({ invokeMethodAsync: async () => null }, new FakeElement("div"), {});
  const frames = [];
  session.sendFrame = (frame) => { frames.push(frame); return Promise.resolve(); };
  session.hello = { clipboard: true, maxFileBytes: 1024 };

  await session.onFrame(0x1a, new TextEncoder().encode("copied on the endpoint"));
  await new Promise((r) => setImmediate(r));
  assert.deepEqual(written, ["copied on the endpoint"]);

  session.sendClipboardText("copied on the endpoint");
  assert.equal(frames.length, 0, "the endpoint already holds this text");
  session.sendClipboardText("new text");
  assert.equal(frames.length, 1);
  assert.equal(frames[0][0], 0x1a);
  assert.equal(new TextDecoder().decode(frames[0].subarray(1)), "new text");

  session.hello = { clipboard: false };
  session.sendClipboardText("not allowed");
  assert.equal(frames.length, 1, "no clipboard text when the policy turns it off");
});

test("pasting files asks for a batch, uploads each file into it and places the batch", async () => {
  installDom();
  const { createSession } = await import("../../src/Fleeto.Web/wwwroot/js/remote-control.js");
  const session = createSession({ invokeMethodAsync: async () => null }, new FakeElement("div"), {});
  session.hello = { clipboard: true, maxFileBytes: 1024 };
  const calls = [];
  const notices = [];
  session.viewer = { notice: (m) => notices.push(m), transfer: () => ({ update() {}, done() {}, failed() {} }) };
  session.request = async (op, params) => {
    calls.push([op, params]);
    return op === "clipboard.begin" ? { batch: 4 } : { count: 2 };
  };
  session.uploadWith = async (op, params, file) => calls.push([op, params.batch, file.name]);

  await session.pasteFiles([{ name: "a.txt", size: 10 }, { name: "b.txt", size: 20 }]);
  assert.deepEqual(calls, [["clipboard.begin", undefined], ["clipboard.upload", 4, "a.txt"], ["clipboard.upload", 4, "b.txt"], ["clipboard.place", { batch: 4 }]]);
  assert.match(notices.at(-1), /2 files are on the endpoint clipboard/);

  calls.length = 0;
  await session.pasteFiles([{ name: "huge.iso", size: 4096 }]);
  assert.equal(calls.length, 0, "a file over the policy cap is refused before anything is sent");
  assert.match(notices.at(-1), /larger than/);
});
