// The viewer of a remote control session (0.3.0 step 3): a toolbar (monitor choice, Ctrl+Alt+Del, Type clipboard, fit) and a canvas that
// shows the endpoint's screen. It draws the tiles the endpoint sends and forwards the mouse and keyboard. It talks to the endpoint through
// the ControlSession (remote-control.js): session.sendControl / session.sendSecureAttention / session.setMonitor.
import { Frame } from "./remote-crypto.mjs";

const updateHeader = 7; // frame uint32 | flags uint8 | count uint16 (after the type byte the session already stripped)
const tileHeader = 13; // x,y,w,h uint16 | format uint8 | length uint32
const FlagLast = 1;
const FormatPNG = 1;

export class Viewer {
  constructor(session, root, options) {
    this.session = session;
    this.root = root;
    this.options = options;
    this.canvas = null;
    this.ctx = null;
    this.width = 0;
    this.height = 0;
    this.monitors = [];
    this.monitor = -2;
    this.disabled = false;
    this.buttons = 0;
    this.lastMove = 0;
    this.fit = true;
    this.boundKeydown = (e) => this.onKey(e, true);
    this.boundKeyup = (e) => this.onKey(e, false);
    this.boundBlur = () => this.releaseKeys();
  }

  render() {
    this.root.replaceChildren();
    const toolbar = el("div", "remote-toolbar remote-control-toolbar");

    this.monitorSelect = el("select", "remote-startselect");
    this.monitorSelect.title = "Monitor";
    this.monitorSelect.addEventListener("change", () => {
      const value = parseInt(this.monitorSelect.value, 10);
      this.session.setMonitor(Number.isNaN(value) ? -1 : value);
    });
    this.monitorSelect.style.display = "none";

    this.cadButton = button("Ctrl + Alt + Del", () => this.session.sendSecureAttention());
    this.typeButton = button("Type clipboard", () => this.typeClipboard());
    this.fitButton = button("Actual size", () => this.toggleFit());

    this.desktopLabel = el("span", "remote-desktop muted");

    toolbar.append(this.monitorSelect, this.cadButton, this.typeButton, this.fitButton, this.desktopLabel);

    this.status = el("div", "remote-error");
    this.surface = el("div", "remote-screen");
    this.canvas = el("canvas", "remote-canvas");
    this.ctx = this.canvas.getContext("2d", { alpha: false });
    this.surface.appendChild(this.canvas);

    this.root.append(toolbar, this.status, this.surface);
    this.bindInput();
  }

  setConnecting() {
    this.status.textContent = "Setting up an encrypted session…";
  }

  setConnected() {
    this.status.textContent = "";
  }

  notice(message) {
    this.status.textContent = message;
  }

  onInfo(info) {
    this.monitors = info.monitors || [];
    this.monitor = info.monitor;
    if (this.width !== info.width || this.height !== info.height) {
      this.width = info.width;
      this.height = info.height;
      this.canvas.width = info.width;
      this.canvas.height = info.height;
      this.applyFit();
    }
    this.desktopLabel.textContent = describeDesktop(info);
    this.buildMonitorSelect();
  }

  buildMonitorSelect() {
    if (this.monitors.length <= 1) {
      this.monitorSelect.style.display = "none";
      return;
    }
    this.monitorSelect.style.display = "";
    this.monitorSelect.replaceChildren();
    const all = el("option");
    all.value = "-1";
    all.textContent = "All monitors";
    all.selected = this.monitor === -1;
    this.monitorSelect.appendChild(all);
    for (const m of this.monitors) {
      const option = el("option");
      option.value = String(m.index);
      option.textContent = `${m.name || "Monitor " + (m.index + 1)}${m.primary ? " (primary)" : ""}`;
      option.selected = m.index === this.monitor;
      this.monitorSelect.appendChild(option);
    }
  }

  // onUpdate draws the tiles of one frame, then acknowledges it so the endpoint sends the next.
  async onUpdate(body, ack) {
    if (body.length < updateHeader) {
      return;
    }
    const view = new DataView(body.buffer, body.byteOffset, body.length);
    const frame = view.getUint32(0);
    const last = (view.getUint8(4) & FlagLast) !== 0;
    const count = view.getUint16(5);
    let offset = updateHeader;
    for (let i = 0; i < count; i++) {
      if (offset + tileHeader > body.length) {
        break;
      }
      const x = view.getUint16(offset);
      const y = view.getUint16(offset + 2);
      const w = view.getUint16(offset + 4);
      const h = view.getUint16(offset + 6);
      const format = view.getUint8(offset + 8);
      const length = view.getUint32(offset + 9);
      offset += tileHeader;
      if (offset + length > body.length) {
        break;
      }
      const data = body.subarray(offset, offset + length);
      offset += length;
      try {
        const bitmap = await createImageBitmap(new Blob([data], { type: format === FormatPNG ? "image/png" : "image/jpeg" }));
        this.ctx.drawImage(bitmap, x, y, w, h);
        bitmap.close();
      } catch {
        // A tile that does not decode is skipped; the next full frame repairs it.
      }
    }
    if (last) {
      ack(frame);
    }
  }

  // --- input -----------------------------------------------------------------

  bindInput() {
    this.canvas.tabIndex = 0;
    this.canvas.addEventListener("pointerdown", (e) => this.onPointer(e, true));
    this.canvas.addEventListener("pointerup", (e) => this.onPointer(e, false));
    this.canvas.addEventListener("pointermove", (e) => this.onPointerMove(e));
    this.canvas.addEventListener("contextmenu", (e) => e.preventDefault());
    this.canvas.addEventListener("wheel", (e) => this.onWheel(e), { passive: false });
    this.canvas.addEventListener("keydown", this.boundKeydown);
    this.canvas.addEventListener("keyup", this.boundKeyup);
    this.canvas.addEventListener("blur", this.boundBlur);
  }

  position(event) {
    const rect = this.canvas.getBoundingClientRect();
    if (rect.width === 0 || rect.height === 0) {
      return null;
    }
    const x = Math.round((event.clientX - rect.left) / rect.width * this.width);
    const y = Math.round((event.clientY - rect.top) / rect.height * this.height);
    return { x: Math.max(0, Math.min(this.width - 1, x)), y: Math.max(0, Math.min(this.height - 1, y)) };
  }

  onPointer(event, down) {
    if (this.disabled) {
      return;
    }
    event.preventDefault();
    this.canvas.focus();
    const p = this.position(event);
    if (!p) {
      return;
    }
    this.buttons = event.buttons;
    this.session.activity();
    this.session.sendControl(Frame.Pointer, { x: p.x, y: p.y, buttons: event.buttons });
  }

  onPointerMove(event) {
    if (this.disabled) {
      return;
    }
    const now = performance.now();
    if (now - this.lastMove < 15 && event.buttons === this.buttons) {
      return;
    }
    this.lastMove = now;
    const p = this.position(event);
    if (!p) {
      return;
    }
    this.buttons = event.buttons;
    this.session.sendControl(Frame.Pointer, { x: p.x, y: p.y, buttons: event.buttons });
  }

  onWheel(event) {
    if (this.disabled) {
      return;
    }
    event.preventDefault();
    const p = this.position(event);
    if (!p) {
      return;
    }
    this.session.activity();
    // The browser's deltaY is positive downward; the endpoint's wheel is positive away from the user.
    this.session.sendControl(Frame.Pointer, {
      x: p.x, y: p.y, buttons: this.buttons,
      wheel: event.deltaY > 0 ? -1 : event.deltaY < 0 ? 1 : 0,
      hwheel: event.deltaX > 0 ? 1 : event.deltaX < 0 ? -1 : 0
    });
  }

  onKey(event, down) {
    if (this.disabled) {
      return;
    }
    event.preventDefault();
    this.session.activity();
    this.session.sendControl(Frame.Key, {
      code: event.code,
      key: event.key,
      down,
      ctrl: event.ctrlKey,
      alt: event.altKey,
      shift: event.shiftKey,
      meta: event.metaKey,
      altGraph: event.getModifierState ? event.getModifierState("AltGraph") : false
    });
  }

  releaseKeys() {
    this.buttons = 0;
    this.session.sendControl(Frame.ReleaseKeys, {});
  }

  async typeClipboard() {
    try {
      const text = await navigator.clipboard.readText();
      if (text) {
        this.session.sendControl(Frame.Type, { text: text.slice(0, 10000) });
        this.notice("");
      }
    } catch {
      this.notice("The browser did not allow reading the clipboard. Click the page, then try again, or allow clipboard access for this site.");
    }
  }

  toggleFit() {
    this.fit = !this.fit;
    this.fitButton.textContent = this.fit ? "Actual size" : "Fit to window";
    this.applyFit();
  }

  applyFit() {
    this.canvas.classList.toggle("remote-canvas-fit", this.fit);
    this.canvas.classList.toggle("remote-canvas-actual", !this.fit);
  }

  disable() {
    this.disabled = true;
    this.buttons = 0;
    for (const control of [this.monitorSelect, this.cadButton, this.typeButton]) {
      if (control) {
        control.disabled = true;
      }
    }
  }

  dispose() {
    this.disable();
    if (this.canvas) {
      this.canvas.removeEventListener("keydown", this.boundKeydown);
      this.canvas.removeEventListener("keyup", this.boundKeyup);
      this.canvas.removeEventListener("blur", this.boundBlur);
    }
  }
}

function describeDesktop(info) {
  const session = info.session ? `Windows session ${info.session}` : "the console";
  if (info.desktop === "Winlogon") {
    return `Sign-in screen or UAC on ${session}`;
  }
  if (info.desktop && info.desktop !== "Default") {
    return `${info.desktop} on ${session}`;
  }
  return session.charAt(0).toUpperCase() + session.slice(1);
}

function el(tag, className) {
  const node = document.createElement(tag);
  if (className) {
    node.className = className;
  }
  return node;
}

function button(label, onClick) {
  const node = el("button", "remote-button");
  node.type = "button";
  node.textContent = label;
  node.addEventListener("click", onClick);
  return node;
}
