// Checks "Copy output" of the remote background terminal (0.7.0) in src/Fleeto.Web/wwwroot/js/remote-ui.mjs: every line of the buffer
// of xterm, the scrollback included, oldest first; a line the terminal wrapped joined to the one before it; the empty rows below the
// prompt left out. Run: node --test tests/browser/remote-terminal.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { terminalLines } from "../../src/Fleeto.Web/wwwroot/js/remote-ui.mjs";

// The part of the buffer API of xterm that terminalLines reads: length, getLine(i).translateToString(true) and isWrapped.
function fakeTerminal(rows) {
  return {
    buffer: {
      active: {
        length: rows.length,
        getLine: (i) => ({ isWrapped: rows[i].wrapped === true, translateToString: (trim) => (trim ? rows[i].text.trimEnd() : rows[i].text) })
      }
    }
  };
}

test("the whole buffer is copied, wrapped lines joined and empty rows at the end left out", () => {
  const terminal = fakeTerminal([
    { text: "PS C:\> Get-ChildItem C:\Users\someone\AppData\Local\Microsoft\Out" },
    { text: "look -Recurse", wrapped: true },
    { text: "" },
    { text: "    Directory: C:\Users   " },
    { text: "PS C:\> " },
    { text: "" },
    { text: "   " }
  ]);

  assert.deepEqual(terminalLines(terminal), [
    "PS C:\> Get-ChildItem C:\Users\someone\AppData\Local\Microsoft\Outlook -Recurse",
    "",
    "    Directory: C:\Users",
    "PS C:\>"
  ]);
});

test("an empty terminal copies nothing", () => {
  assert.deepEqual(terminalLines(fakeTerminal([{ text: "" }, { text: "" }])), []);
});
