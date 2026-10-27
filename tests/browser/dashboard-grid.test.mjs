// Checks the grid script of the dashboard designer in src/Fleeto.Web/wwwroot/js/dashboard-grid.mjs: pointer and key input become
// columns and rows that the server is asked to place, a click selects, and a tile type from the palette lands on the cell under the
// pointer. The script never places a tile itself. Run: node --test tests/browser/dashboard-grid.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { attach } from "../../src/Fleeto.Web/wwwroot/js/dashboard-grid.mjs";

// A grid of 1188px wide: 12 columns of 90px with gaps of 12px, so a column step is 99px and a row step 76px.
function installPage() {
  const listeners = {};
  const documentListeners = {};
  const grid = { getBoundingClientRect: () => ({ left: 100, top: 50, width: 1176, height: 800 }) };
  const tile = {
    dataset: { tileId: "a", x: "2", y: "1", w: "2", h: "2" },
    style: {},
    classList: { items: new Set(), add(c) { this.items.add(c); }, remove(c) { this.items.delete(c); } },
    setPointerCapture() {}, releasePointerCapture() {}, focus() {},
    closest: (selector) => (selector === "[data-tile-id]" ? tile : null)
  };
  const handle = { closest: (selector) => (selector === "[data-resize]" ? handle : selector === "[data-tile-id]" ? tile : null) };
  const canvas = {
    addEventListener: (type, handler) => { listeners[type] = handler; },
    removeEventListener: (type) => { delete listeners[type]; },
    querySelector: () => grid,
    contains: () => true
  };
  globalThis.HTMLElement = class {};
  globalThis.document = {
    addEventListener: (type, handler) => { documentListeners[type] = handler; },
    removeEventListener: (type) => { delete documentListeners[type]; }
  };
  const calls = [];
  const dotnet = { invokeMethodAsync: (name, ...args) => { calls.push([name, ...args]); return Promise.resolve(); } };
  return { listeners, documentListeners, canvas, tile, handle, calls, dotnet };
}

function pointer(target, x, y, extra = {}) {
  return { button: 0, pointerId: 1, target, clientX: x, clientY: y, preventDefault() {}, ...extra };
}

test("a click without moving selects the tile", async () => {
  const page = installPage();
  attach(page.canvas, null, page.dotnet);
  page.listeners.pointerdown(pointer(page.tile, 300, 200));
  page.listeners.pointermove(pointer(page.tile, 302, 201));
  await page.listeners.pointerup(pointer(page.tile, 302, 201));
  assert.deepEqual(page.calls, [["SelectTile", "a"]]);
});

test("dragging asks the server where every tile goes once per new cell and commits on release", async () => {
  const page = installPage();
  attach(page.canvas, null, page.dotnet);
  page.listeners.pointerdown(pointer(page.tile, 300, 200));
  page.listeners.pointermove(pointer(page.tile, 300 + 99, 200));
  page.listeners.pointermove(pointer(page.tile, 300 + 105, 205));
  page.listeners.pointermove(pointer(page.tile, 300 + 2 * 99, 200 + 76));
  assert.equal(page.tile.style.transform, "translate(198px, 76px)");
  await page.listeners.pointerup(pointer(page.tile, 300 + 2 * 99, 200 + 76));
  assert.deepEqual(page.calls, [
    ["PreviewPlace", "a", 3, 1, 2, 2, false],
    ["PreviewPlace", "a", 4, 2, 2, 2, false],
    ["CommitPlace", "a", 4, 2, 2, 2]
  ]);
  assert.equal(page.tile.style.transform, "");
});

test("a tile is never proposed outside the grid", async () => {
  const page = installPage();
  attach(page.canvas, null, page.dotnet);
  page.listeners.pointerdown(pointer(page.tile, 300, 200));
  page.listeners.pointermove(pointer(page.tile, 300 + 20 * 99, 200 - 10 * 76));
  await page.listeners.pointerup(pointer(page.tile, 300 + 20 * 99, 200 - 10 * 76));
  assert.deepEqual(page.calls.at(-1), ["CommitPlace", "a", 10, 0, 2, 2]);
});

test("the resize handle changes the size, not the place", async () => {
  const page = installPage();
  attach(page.canvas, null, page.dotnet);
  page.listeners.pointerdown(pointer(page.handle, 500, 300));
  page.listeners.pointermove(pointer(page.handle, 500 + 2 * 99, 300 + 76));
  assert.ok(!page.tile.style.transform);
  await page.listeners.pointerup(pointer(page.handle, 500 + 2 * 99, 300 + 76));
  assert.deepEqual(page.calls, [["PreviewPlace", "a", 2, 1, 4, 3, true], ["CommitPlace", "a", 2, 1, 4, 3]]);
  assert.ok(!page.tile.style.transform);
});

test("keys move, resize and remove the focused tile, and undo works outside fields", () => {
  const page = installPage();
  attach(page.canvas, null, page.dotnet);
  const key = (k, extra = {}) => ({ key: k, target: page.tile, shiftKey: false, preventDefault() {}, ...extra });
  page.listeners.keydown(key("ArrowRight"));
  page.listeners.keydown(key("ArrowDown", { shiftKey: true }));
  page.listeners.keydown(key("Delete"));
  page.documentListeners.keydown({ key: "z", ctrlKey: true, shiftKey: false, target: {}, preventDefault() {} });
  page.documentListeners.keydown({ key: "y", ctrlKey: true, shiftKey: false, target: {}, preventDefault() {} });
  assert.deepEqual(page.calls, [
    ["KeyPlace", "a", 1, 0, false],
    ["KeyPlace", "a", 0, 1, true],
    ["RemoveTile", "a"],
    ["Undo"],
    ["Redo"]
  ]);
});

test("a tile type dropped from the palette lands on the cell under the pointer", () => {
  const page = installPage();
  attach(page.canvas, null, page.dotnet);
  const data = { "application/x-fleeto-tile": "alerts.open" };
  const transfer = { types: Object.keys(data), getData: (t) => data[t] ?? "", dropEffect: "" };
  let prevented = false;
  page.listeners.dragover({ dataTransfer: transfer, preventDefault() { prevented = true; } });
  assert.ok(prevented);
  page.listeners.drop({ dataTransfer: transfer, clientX: 100 + 5 * 99 + 10, clientY: 50 + 3 * 76 + 5, preventDefault() {} });
  assert.deepEqual(page.calls, [["DropType", "alerts.open", 5, 3]]);
});

test("dispose removes every listener", () => {
  const page = installPage();
  const grid = attach(page.canvas, null, page.dotnet);
  grid.dispose();
  assert.deepEqual(Object.keys(page.listeners), []);
  assert.deepEqual(Object.keys(page.documentListeners), []);
});
