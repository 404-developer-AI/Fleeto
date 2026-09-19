// The viewer of a remote control session (0.3.0 step 3): a toolbar (monitor choice, Ctrl+Alt+Del, Type clipboard, fit) and a canvas that
// shows the endpoint's screen. It draws the tiles the endpoint sends and forwards the mouse and keyboard. It talks to the endpoint through
// the ControlSession (remote-control.js): session.sendControl / session.sendSecureAttention / session.setMonitor.
// Step 4 adds the technicians in the session with their pointers, the consent prompt, the clipboard (paste and drop files, text both ways)
// and the files copied on the endpoint. Step 5 adds H.264 (remote-video.mjs) next to the tiles, and the statistics of the stream.
import { Frame } from "./remote-crypto.mjs";
import { ScreenStats, VideoStream } from "./remote-video.mjs";

const updateHeader = 7; // frame uint32 | flags uint8 | count uint16 (after the type byte the session already stripped)
const tileHeader = 13; // x,y,w,h uint16 | format uint8 | length uint32
const FlagLast = 1;
const FormatPNG = 1;

// How long the browser waits for its paste event before the paste shortcut goes to the endpoint anyway.
const pasteWait = 300;

// Pointer colors of the other technicians: distinct on the dark screen background, in both themes.
const peerColors = ["#F59E0B", "#38BDF8", "#F472B6", "#A3E635", "#C084FC", "#FB7185", "#2DD4BF"];

// What the two clipboard actions need a word about, the first times a technician does them. "Do not show this again" is remembered in this
// browser, per technician who signs in to it, like the collapsed sidebar.
const hints = {
  paste: {
    key: "fleeto.remote.hint.paste-files",
    message: "The files are sent to the endpoint first. When the transfer is done, press Ctrl+V on the endpoint, where you want them."
  },
  copied: {
    key: "fleeto.remote.hint.copied-files",
    message: "Files copied on the endpoint cannot be put on your own clipboard; a browser may not do that. Use the download button below to save them."
  }
};

function hintDismissed(key) {
  try {
    return window.localStorage.getItem(key) === "1";
  } catch {
    return false; // a browser that keeps no storage shows the hint every time
  }
}

function dismissHint(key) {
  try {
    window.localStorage.setItem(key, "1");
  } catch {
    // Nothing to remember it with; the hint returns next time.
  }
}

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
    this.participants = [];
    this.peers = new Map();
    this.pendingPaste = null;
    this.ignoreUp = null;
    this.consentTimer = null;
    this.boundKeydown = (e) => this.onKey(e, true);
    this.boundKeyup = (e) => this.onKey(e, false);
    this.boundBlur = () => this.releaseKeys();
    this.boundPaste = (e) => this.onPaste(e);
    this.boundResize = () => this.placePeers();
    // The screen stream: the codec the endpoint uses (FrameInfo), the H.264 decoder, the statistics and when the last frame was acknowledged.
    this.stream = {};
    this.video = null;
    this.stats = new ScreenStats();
    this.lastAckAt = null;
    this.statsTimer = null;
    this.toldFallback = null;
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
    // Ctrl+Alt+Del is a Windows sign-in key; a Linux endpoint (0.3.0 step 6) has no use for it, so the button is not there.
    if (this.session.hello?.platform === "linux") {
      this.cadButton.style.display = "none";
    }
    this.typeButton = button("Type clipboard", () => this.typeClipboard());
    this.fitButton = button("Actual size", () => this.toggleFit());

    this.desktopLabel = el("span", "remote-desktop muted");
    this.statsLabel = el("span", "remote-stats muted");
    this.participantList = el("span", "remote-participants");

    toolbar.append(this.monitorSelect, this.cadButton, this.typeButton, this.fitButton, this.desktopLabel, this.statsLabel, this.participantList);

    this.status = el("div", "remote-error");
    this.hintBar = el("div", "remote-hint");
    this.clipboardBar = el("div", "remote-clipboard");
    this.copiedFiles = el("div", "remote-copied");
    this.transfers = el("div", "remote-transfers");
    this.clipboardBar.append(this.copiedFiles, this.transfers);

    this.surface = el("div", "remote-screen");
    this.canvas = el("canvas", "remote-canvas");
    this.ctx = this.canvas.getContext("2d", { alpha: false });
    this.peerLayer = el("div", "remote-peers");
    this.surface.append(this.canvas, this.peerLayer);

    this.root.append(toolbar, this.status, this.hintBar, this.clipboardBar, this.surface);
    this.bindInput();
    this.statsTimer = setInterval(() => this.renderStats(), 1000);
    // Outside a browser (the Node tests) the timer must not keep the process alive; a browser's timer id has no unref.
    this.statsTimer?.unref?.();
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
    const codec = info.codec || "tiles";
    if (codec !== (this.stream.codec || "tiles")) {
      this.stats.reset();
    }
    if (codec !== "h264" && this.video) {
      this.video.reset();
    }
    this.stream = { codec, encoder: info.encoder || "", capture: info.capture || "", legacy: !info.codec };
    if (info.fallback && info.fallback !== this.toldFallback) {
      this.notice(info.fallback);
    }
    this.toldFallback = info.fallback || null;
    this.renderStats();
  }

  // ackFrame tells the endpoint a frame is drawn, so it captures the next; the time is kept for the latency estimate.
  ackFrame(frame, ack) {
    this.lastAckAt = typeof performance !== "undefined" ? performance.now() : Date.now();
    ack(frame);
  }

  // onVideo takes a part of an H.264 frame (0.3.0 step 5); the frame is drawn and acknowledged once it is decoded.
  onVideo(body, ack) {
    if (!this.video) {
      this.video = new VideoStream({
        draw: (frame) => this.ctx.drawImage(frame, 0, 0),
        ack: (frame) => this.ackFrame(frame, ack),
        failed: (message) => this.session.videoFailed?.(message),
        stats: this.stats,
        lastAckAt: () => this.lastAckAt
      });
    }
    this.video.push(body);
  }

  // renderStats shows how the screen travels: the codec, frames a second, the bit rate and, for H.264, the latency estimate.
  renderStats() {
    if (!this.statsLabel) {
      return;
    }
    const s = this.stats.summary();
    const parts = [describeCodec(this.stream)];
    if (s.fps > 0 || s.bitsPerSecond > 0) {
      parts.push(`${s.fps} fps`, formatBitrate(s.bitsPerSecond));
    }
    if (this.stream.codec === "h264" && s.latencyMs !== null) {
      parts.push(`about ${s.latencyMs} ms`);
    }
    this.statsLabel.textContent = parts.join(" · ");
    const title = [];
    if (this.stream.capture) {
      title.push({ dxgi: "Captured with desktop duplication.", gdi: "Captured with GDI.", x11: "Captured from the X11 display." }[this.stream.capture] ?? "");
    }
    if (this.stream.legacy) {
      title.push("The agent on this endpoint sends tiles only. Update it to send H.264.");
    }
    if (this.stream.codec === "h264" && s.breakdown) {
      const b = s.breakdown;
      title.push(`Latency estimate: endpoint ${Math.round(b.endpoint)} ms, network ${Math.round(b.network)} ms, ` +
        `decoding ${Math.round(b.browser)} ms.`);
    }
    this.statsLabel.title = title.join(" ");
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
    this.stats.received(body.length + 1);
    if (last) {
      this.stats.drawn();
      this.ackFrame(frame, ack);
    }
  }

  // --- technicians -----------------------------------------------------------

  onParticipants(list) {
    this.participants = list;
    this.participantList.replaceChildren();
    list.forEach((p, i) => {
      const chip = el("span", "remote-participant");
      const dot = el("span", "remote-participant-dot");
      dot.style.background = p.you ? "var(--mud-palette-primary)" : peerColors[i % peerColors.length];
      chip.append(dot, document.createTextNode(p.you ? `${p.name} (you)` : p.name));
      this.participantList.appendChild(chip);
    });
    // A technician who left takes their pointer along.
    for (const id of [...this.peers.keys()]) {
      if (!list.some((p) => p.id === id)) {
        this.peers.get(id).marker.remove();
        this.peers.delete(id);
      }
    }
  }

  onPeerPointer(pointer) {
    const index = this.participants.findIndex((p) => p.id === pointer.id);
    if (index < 0 || this.participants[index].you) {
      return;
    }
    let peer = this.peers.get(pointer.id);
    if (!peer) {
      const marker = el("div", "remote-peer");
      const color = peerColors[index % peerColors.length];
      marker.style.setProperty("--peer-color", color);
      const label = el("span", "remote-peer-name");
      label.textContent = this.participants[index].name;
      marker.appendChild(label);
      this.peerLayer.appendChild(marker);
      peer = { marker };
      this.peers.set(pointer.id, peer);
    }
    peer.x = pointer.x;
    peer.y = pointer.y;
    this.placePeer(peer);
  }

  placePeers() {
    for (const peer of this.peers.values()) {
      this.placePeer(peer);
    }
  }

  // placePeer puts a pointer marker where the endpoint pixel is shown, whatever the scaling and scrolling of the canvas.
  placePeer(peer) {
    if (!this.width || !this.height || typeof peer.x !== "number") {
      return;
    }
    const canvasRect = this.canvas.getBoundingClientRect();
    const surfaceRect = this.surface.getBoundingClientRect();
    const left = canvasRect.left - surfaceRect.left + this.surface.scrollLeft + peer.x / this.width * canvasRect.width;
    const top = canvasRect.top - surfaceRect.top + this.surface.scrollTop + peer.y / this.height * canvasRect.height;
    peer.marker.style.transform = `translate(${Math.round(left)}px, ${Math.round(top)}px)`;
  }

  // --- consent ---------------------------------------------------------------

  onConsent(consent) {
    clearInterval(this.consentTimer);
    this.consentTimer = null;
    if (consent.state === "waiting") {
      let left = consent.secondsLeft || 0;
      const show = () => {
        this.status.textContent = left > 0 ? `${consent.message} ${left} s left.` : consent.message;
      };
      show();
      if (left > 0) {
        this.consentTimer = setInterval(() => {
          left = Math.max(0, left - 1);
          show();
          if (left === 0) {
            clearInterval(this.consentTimer);
            this.consentTimer = null;
          }
        }, 1000);
      }
      return;
    }
    this.status.textContent = consent.message || "";
  }

  // --- clipboard -------------------------------------------------------------

  // hint explains a clipboard action the first times a technician does it, until they say they know.
  hint(kind) {
    const hint = hints[kind];
    if (!hint || !this.hintBar || this.hintBar.dataset?.kind === kind || hintDismissed(hint.key)) {
      return;
    }
    this.hintBar.replaceChildren();
    if (this.hintBar.dataset) {
      this.hintBar.dataset.kind = kind;
    }
    const text = el("span");
    text.textContent = hint.message;
    const close = () => {
      this.hintBar.replaceChildren();
      if (this.hintBar.dataset) {
        this.hintBar.dataset.kind = "";
      }
    };
    const got = button("Got it", close);
    const never = button("Do not show this again", () => {
      dismissHint(hint.key);
      close();
    });
    never.classList.add("remote-icon-button");
    this.hintBar.append(text, got, never);
  }

  onClipboardFiles(offer) {
    this.copiedFiles.replaceChildren();
    const files = offer.files || [];
    if (files.length === 0 && !offer.folders) {
      return;
    }
    const title = el("span", "remote-copied-title");
    const parts = [];
    if (files.length > 0) {
      parts.push(files.length === 1 ? "1 file copied on the endpoint" : `${files.length} files copied on the endpoint`);
    }
    if (offer.folders) {
      parts.push(offer.folders === 1 ? "1 folder cannot be downloaded" : `${offer.folders} folders cannot be downloaded`);
    }
    title.textContent = parts.join("; ") + (files.length > 0 ? ":" : ".");
    this.copiedFiles.appendChild(title);
    if (files.length > 0) {
      this.hint("copied");
    }
    for (const file of files) {
      const item = button(`${file.name} (${formatBytes(file.size)})`, () => this.session.downloadCopied(file.index, file.name));
      item.classList.add("remote-icon-button");
      item.title = "Download";
      this.copiedFiles.appendChild(item);
    }
  }

  clipboardPending(pending) {
    if (pending) {
      this.notice("Text was copied on the endpoint. Click the screen to put it on your clipboard.");
    } else if (this.status.textContent.startsWith("Text was copied on the endpoint")) {
      this.notice("");
    }
  }

  // transfer shows the progress of a pasted or downloaded file.
  transfer(label) {
    const row = el("div", "remote-transfer");
    const text = el("span");
    text.textContent = label;
    const bar = el("div", "remote-progress");
    const fill = el("div", "remote-progress-fill");
    bar.appendChild(fill);
    row.append(text, bar);
    this.transfers.appendChild(row);
    return {
      update: (done, total) => {
        fill.style.width = total > 0 ? `${Math.round(done / total * 100)}%` : "100%";
      },
      done: () => {
        fill.style.width = "100%";
        setTimeout(() => row.remove(), 1500);
      },
      failed: (message) => {
        row.classList.add("failed");
        text.textContent = `${label}: ${message}`;
        setTimeout(() => row.remove(), 10000);
      },
      remove: () => row.remove()
    };
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
    this.surface.addEventListener("dragover", (e) => this.onDragOver(e));
    this.surface.addEventListener("dragleave", () => this.surface.classList.remove("remote-drop"));
    this.surface.addEventListener("drop", (e) => this.onDrop(e));
    this.surface.addEventListener("scroll", this.boundResize);
    if (typeof document.addEventListener === "function") {
      document.addEventListener("paste", this.boundPaste);
    }
    if (typeof window !== "undefined" && typeof window.addEventListener === "function") {
      window.addEventListener("resize", this.boundResize);
    }
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
    if (down) {
      // A click counts as a user gesture: text copied on the endpoint can go to this computer's clipboard now.
      this.session.flushRemoteClipboard?.();
    }
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
    const key = {
      code: event.code,
      key: event.key,
      down,
      ctrl: event.ctrlKey,
      alt: event.altKey,
      shift: event.shiftKey,
      meta: event.metaKey,
      altGraph: event.getModifierState ? event.getModifierState("AltGraph") : false
    };
    this.session.activity();
    if (down && this.session.clipboardEnabled?.() && isPasteShortcut(event)) {
      // Not prevented: the browser fires its paste event, the only way to read this computer's clipboard without a permission prompt. The
      // shortcut reaches the endpoint once the clipboard is there.
      this.pendingPaste = { down: key, up: null };
      clearTimeout(this.pasteTimer);
      this.pasteTimer = setTimeout(() => this.flushPaste(), pasteWait);
      return;
    }
    event.preventDefault();
    if (!down && this.pendingPaste && this.pendingPaste.down.code === event.code) {
      this.pendingPaste.up = key;
      return;
    }
    if (!down && this.ignoreUp === event.code) {
      this.ignoreUp = null;
      return;
    }
    this.session.sendControl(Frame.Key, key);
  }

  // onPaste takes the clipboard of this computer while the screen has the focus: files are placed on the endpoint clipboard, text is
  // synchronised and then the paste shortcut goes to the endpoint.
  onPaste(event) {
    if (this.disabled || !isFocused(this.canvas)) {
      return;
    }
    event.preventDefault();
    const data = event.clipboardData;
    const files = data?.files ? [...data.files] : [];
    if (files.length > 0) {
      if (this.session.filesAlreadyPlaced?.(files)) {
        // They are on the endpoint clipboard already: this paste belongs there.
        this.flushPaste();
        return;
      }
      // The files still have to travel; the technician pastes on the endpoint once they are there.
      this.cancelPaste();
      this.session.pasteFiles(files);
      return;
    }
    const text = data?.getData ? data.getData("text/plain") : "";
    if (text) {
      this.session.sendClipboardText(text);
    }
    this.flushPaste();
  }

  flushPaste() {
    clearTimeout(this.pasteTimer);
    const pending = this.pendingPaste;
    this.pendingPaste = null;
    if (!pending) {
      return;
    }
    this.session.sendControl(Frame.Key, pending.down);
    if (pending.up) {
      this.session.sendControl(Frame.Key, pending.up);
    }
  }

  cancelPaste() {
    clearTimeout(this.pasteTimer);
    const pending = this.pendingPaste;
    this.pendingPaste = null;
    if (pending && !pending.up) {
      // The key is still held on this computer: its release must not reach the endpoint alone.
      this.ignoreUp = pending.down.code;
    }
  }

  onDragOver(event) {
    if (this.disabled || !event.dataTransfer || ![...(event.dataTransfer.types || [])].includes("Files")) {
      return;
    }
    event.preventDefault();
    event.dataTransfer.dropEffect = this.session.clipboardEnabled?.() ? "copy" : "none";
    this.surface.classList.add("remote-drop");
  }

  onDrop(event) {
    this.surface.classList.remove("remote-drop");
    if (this.disabled || !event.dataTransfer) {
      return;
    }
    event.preventDefault();
    const items = [...(event.dataTransfer.items || [])];
    const folders = items.filter((item) => item.webkitGetAsEntry?.()?.isDirectory).length;
    const files = [...(event.dataTransfer.files || [])].filter((file, i) => !items[i]?.webkitGetAsEntry?.()?.isDirectory);
    if (folders > 0) {
      this.notice(files.length > 0 ? "Folders cannot be pasted; the files are." : "Folders cannot be pasted. Drop the files in them instead.");
    }
    if (files.length > 0) {
      this.session.pasteFiles(files);
    }
  }

  releaseKeys() {
    this.buttons = 0;
    this.pendingPaste = null;
    clearTimeout(this.pasteTimer);
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
    this.placePeers();
  }

  disable() {
    this.disabled = true;
    this.buttons = 0;
    clearInterval(this.consentTimer);
    clearInterval(this.statsTimer);
    this.statsTimer = null;
    if (this.video) {
      this.video.close();
      this.video = null;
    }
    for (const control of [this.monitorSelect, this.cadButton, this.typeButton, ...(this.copiedFiles?.querySelectorAll?.("button") ?? [])]) {
      if (control) {
        control.disabled = true;
      }
    }
  }

  dispose() {
    this.disable();
    clearTimeout(this.pasteTimer);
    if (this.canvas) {
      this.canvas.removeEventListener("keydown", this.boundKeydown);
      this.canvas.removeEventListener("keyup", this.boundKeyup);
      this.canvas.removeEventListener("blur", this.boundBlur);
    }
    if (typeof document.removeEventListener === "function") {
      document.removeEventListener("paste", this.boundPaste);
    }
    if (typeof window !== "undefined" && typeof window.removeEventListener === "function") {
      window.removeEventListener("resize", this.boundResize);
    }
  }
}

function isPasteShortcut(event) {
  return ((event.ctrlKey || event.metaKey) && !event.altKey && event.code === "KeyV") || (event.shiftKey && event.code === "Insert");
}

function isFocused(node) {
  return typeof document !== "undefined" && document.activeElement === node;
}

function formatBytes(bytes) {
  if (bytes < 1024) {
    return `${bytes} B`;
  }
  const units = ["KB", "MB", "GB"];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value.toFixed(value < 10 ? 1 : 0)} ${units[unit]}`;
}

function describeCodec(stream) {
  if (stream.codec === "h264") {
    return stream.encoder ? `H.264, ${stream.encoder} encoder` : "H.264";
  }
  return "Tiles";
}

function formatBitrate(bitsPerSecond) {
  if (bitsPerSecond >= 1000000) {
    return `${(bitsPerSecond / 1000000).toFixed(1)} Mbit/s`;
  }
  return `${Math.round(bitsPerSecond / 1000)} kbit/s`;
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
