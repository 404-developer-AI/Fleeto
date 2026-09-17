// Checks the download side of remote background file transfers in src/Fleeto.Web/wwwroot/js/remote.js: the endpoint starts sending as
// soon as it answers the download request, so chunks and even the end of a small file can arrive before the page knows the transfer.
// Those frames must be kept and written in order, not dropped. Run: node --test tests/browser/remote-transfers.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { createSession } from "../../src/Fleeto.Web/wwwroot/js/remote.js";

// A page without the File System Access API: the download is collected and saved through a link, which this stub captures.
function installPage() {
  const saved = [];
  globalThis.window = {};
  globalThis.document = {
    createElement: () => ({ click() {}, remove() {} }),
    body: { appendChild() {} }
  };
  globalThis.URL.createObjectURL = (blob) => {
    saved.push(blob);
    return "blob:test";
  };
  globalThis.URL.revokeObjectURL = () => {};
  return saved;
}

function chunk(transfer, bytes) {
  const body = new Uint8Array(4 + bytes.length);
  new DataView(body.buffer).setUint32(0, transfer);
  body.set(bytes, 4);
  return body;
}

function newSession(respond) {
  const session = createSession({ invokeMethodAsync: async () => null }, null, {});
  session.state = "connected";
  session.sendFrame = () => Promise.resolve();
  session.request = respond;
  return session;
}

test("frames that arrive before the transfer is known are written in order", async () => {
  const saved = installPage();
  let session;
  session = newSession(async () => {
    // The endpoint already sent the whole file and its end before the answer reaches the page.
    await session.onChunk(chunk(7, Uint8Array.of(1, 2)));
    await session.onChunk(chunk(7, Uint8Array.of(3)));
    await session.onTransfer({ transfer: 7, kind: "end", bytes: 3 });
    return { transfer: 7, size: 3, name: "a.bin" };
  });

  const result = await session.download("C:\\a.bin", "a.bin");
  assert.equal(result.size, 3);
  assert.equal(saved.length, 1);
  assert.deepEqual(new Uint8Array(await saved[0].arrayBuffer()), Uint8Array.of(1, 2, 3));
  assert.equal(session.downloads.size, 0, "a finished download is not left registered");
  assert.equal(session.early.size, 0);
});

test("frames after the answer are delivered to the registered download", async () => {
  const saved = installPage();
  const session = newSession(async () => ({ transfer: 9, size: 4, name: "b.bin" }));

  const pending = session.download("/tmp/b.bin", "b.bin");
  await new Promise((resolve) => setTimeout(resolve, 0));
  await session.onChunk(chunk(9, Uint8Array.of(1, 2)));
  await session.onChunk(chunk(9, Uint8Array.of(3, 4)));
  await session.onTransfer({ transfer: 9, kind: "end", bytes: 4 });

  const result = await pending;
  assert.equal(result.size, 4);
  assert.deepEqual(new Uint8Array(await saved[0].arrayBuffer()), Uint8Array.of(1, 2, 3, 4));
});

test("an error the endpoint sends before the answer fails the download", async () => {
  installPage();
  let session;
  session = newSession(async () => {
    await session.onTransfer({ transfer: 3, kind: "error", error: "the file could not be read" });
    return { transfer: 3, size: 10, name: "c.bin" };
  });
  await assert.rejects(session.download("/tmp/c.bin", "c.bin"), /could not be read/);
  assert.equal(session.downloads.size, 0);
});
