// The browser side of a remote control session (0.3.0 step 3): the same key exchange and encrypted relay as remote background (remote.js),
// but the session carries the screen. The endpoint sends tiles (change detection, PNG for text and JPEG for the rest); this draws them on
// a canvas and sends the mouse and keyboard back. The session key never leaves this page; the gateway relays ciphertext it cannot read.
// Step 4 adds the clipboard (text both ways, files pasted or dropped into the window, files copied on the endpoint offered for download),
// the other technicians in the session and their pointers, and the consent prompt on the endpoint. Step 5 asks the endpoint for H.264 where
// this browser decodes it (WebCodecs); the tiles stay the fallback.
import { deriveSessionKeys, Frame, FrameCipher, fromBase64, generateBrowserKey, supported as cryptoSupported, toBase64, wipeKeys } from "./remote-crypto.mjs";
import { Viewer } from "./remote-control-ui.mjs";
import { installTransfers, transferState } from "./remote-transfers.mjs";
import { decodableCodecs } from "./remote-video.mjs";

/** After this many decoder failures in one window, the screen stays on tiles. */
const maxVideoFailures = 2;

const encoder = new TextEncoder();
const decoder = new TextDecoder();

/** The most clipboard text that is synchronised, as UTF-8 (agent/internal/screen MaxClipboardBytes). */
export const MaxClipboardBytes = 512 * 1024;

export function supported() {
  return typeof WebSocket === "function" && typeof createImageBitmap === "function" && cryptoSupported();
}

function relayUrl(configured, participantId) {
  if (configured) {
    return configured.replace(/\/?$/, "/") + participantId;
  }
  const scheme = window.location.protocol === "https:" ? "wss:" : "ws:";
  return `${scheme}//${window.location.host}/relay/v1/sessions/${participantId}`;
}

function jsonFrame(type, body) {
  const json = encoder.encode(JSON.stringify(body ?? {}));
  const out = new Uint8Array(1 + json.length);
  out[0] = type;
  out.set(json, 1);
  return out;
}

class ControlSession {
  constructor(dotnet, container, options) {
    this.dotnet = dotnet;
    this.container = container;
    this.options = options;
    this.socket = null;
    this.send = null;
    this.receive = null;
    this.state = "idle";
    this.queue = Promise.resolve();
    this.sendQueue = Promise.resolve();
    this.viewer = null;
    this.hello = null;
    this.monitor = typeof options.monitor === "number" ? options.monitor : -2;
    // Auto reconnect (decided 2026-09-17): a dropped session opens a new one in this same window, up to a few tries.
    this.reconnects = 0;
    // The clipboard text both sides hold; it is never sent back to where it came from.
    this.clipboardText = null;
    // Text copied on the endpoint that could not be written to this computer's clipboard yet (the window had no focus).
    this.pendingClipboard = null;
    // Text copied on the endpoint without the technician copying in the window: offered, never written by itself.
    this.offeredClipboard = null;
    // The files last placed on the endpoint clipboard; pasting them again pastes them on the endpoint instead of sending them once more.
    this.placedFiles = null;
    // The codecs this browser decodes besides tiles (null until checked), and how often the H.264 decoder failed.
    this.codecs = null;
    this.videoFailures = 0;
    Object.assign(this, transferState());
    this.onUnload = () => this.end(true);
    this.onFocus = () => this.flushRemoteClipboard();
  }

  async start() {
    this.reconnects = 0;
    await this.open();
  }

  async open() {
    this.report("connecting");
    if (this.viewer) {
      this.viewer.setConnecting();
    }
    try {
      if (this.codecs === null) {
        this.codecs = await decodableCodecs();
      }
      const key = await generateBrowserKey();
      this.browserKey = key;
      const ticket = await this.dotnet.invokeMethodAsync("RequestTicket", toBase64(key.publicKey));
      if (!ticket || ticket.problem) {
        this.fail(ticket?.problem ?? "The session could not be opened. Try again.");
        return;
      }
      this.ticket = ticket;
      this.tokenPayload = fromBase64(ticket.token);
      this.connect();
    } catch (error) {
      this.fail("The session could not be opened: " + (error?.message ?? error));
    }
  }

  connect() {
    const socket = new WebSocket(relayUrl(this.options.relayUrl, this.ticket.participantId));
    socket.binaryType = "arraybuffer";
    this.socket = socket;
    socket.onopen = () => {
      socket.send(JSON.stringify({ token: this.ticket.token, signature: this.ticket.signature, keyId: this.ticket.keyId }));
    };
    socket.onmessage = (event) => {
      this.queue = this.queue.then(() => this.onMessage(event.data)).catch((error) => this.fail(error?.message ?? String(error)));
    };
    socket.onclose = (event) => {
      if (this.state === "connecting") {
        this.retryOrFail(event.reason ? `The relay closed the connection (${event.reason}).` : "The relay closed the connection before the session started.");
      } else if (this.state === "connected") {
        this.retryOrFail("The connection to the endpoint was lost.");
      }
    };
    window.addEventListener("beforeunload", this.onUnload);
    window.addEventListener("focus", this.onFocus);
  }

  async onMessage(data) {
    if (typeof data === "string") {
      const message = JSON.parse(data);
      if (message.type === "error") {
        this.retryOrFail(message.message);
      } else if (message.type === "ready") {
        const keys = await deriveSessionKeys(this.browserKey.privateKey, this.browserKey.publicKey, this.tokenPayload, fromBase64(message.endpointPublicKey),
          fromBase64(message.signature), fromBase64(message.certificatePublicKey), this.ticket.fingerprints);
        this.send = await FrameCipher.create(keys.browserToEndpoint);
        this.receive = await FrameCipher.create(keys.endpointToBrowser);
        wipeKeys(keys);
        this.browserKey = null;
      }
      return;
    }
    if (!this.receive) {
      throw new Error("The relay sent data before the session was set up.");
    }
    const plaintext = await this.receive.open(new Uint8Array(data));
    await this.onFrame(plaintext[0], plaintext.subarray(1));
  }

  async onFrame(type, body) {
    switch (type) {
      case Frame.Hello:
        this.hello = JSON.parse(decoder.decode(body));
        this.state = "connected";
        this.report("connected");
        this.startViewer();
        this.sendControl(Frame.Start, this.startBody(this.monitor));
        break;
      case Frame.Info:
        // The endpoint's helper answered, so the screen works: this is a healthy session, not a crash loop. Only now is the reconnect
        // counter cleared, so a session that connects but dies before showing anything still counts toward the retry cap.
        this.reconnects = 0;
        if (this.viewer) {
          this.viewer.onInfo(JSON.parse(decoder.decode(body)));
        }
        break;
      case Frame.Update:
        if (this.viewer) {
          await this.viewer.onUpdate(body, (frame) => this.sendControl(Frame.Ack, { frame }));
        }
        break;
      case Frame.Video:
        if (this.viewer) {
          this.viewer.onVideo(body, (frame) => this.sendControl(Frame.Ack, { frame }));
        }
        break;
      case Frame.ControlNotice:
        if (this.viewer) {
          this.viewer.notice(JSON.parse(decoder.decode(body)).message ?? "");
        }
        break;
      case Frame.Participants:
        if (this.viewer) {
          this.viewer.onParticipants(JSON.parse(decoder.decode(body)).participants ?? []);
        }
        break;
      case Frame.PeerPointer:
        if (this.viewer) {
          this.viewer.onPeerPointer(JSON.parse(decoder.decode(body)));
        }
        break;
      case Frame.Consent:
        if (this.viewer) {
          this.viewer.onConsent(JSON.parse(decoder.decode(body)));
        }
        break;
      case Frame.Clipboard:
        this.onRemoteClipboard(decoder.decode(body));
        break;
      case Frame.ClipboardFiles:
        // The endpoint clipboard changed: pasting files again has to send them again.
        this.placedFiles = null;
        if (this.viewer) {
          this.viewer.onClipboardFiles(JSON.parse(decoder.decode(body)));
        }
        break;
      case Frame.Response:
        this.resolveRequest(JSON.parse(decoder.decode(body)));
        break;
      case Frame.Chunk:
        await this.onChunk(body);
        break;
      case Frame.Transfer:
        await this.onTransfer(JSON.parse(decoder.decode(body)));
        break;
      case Frame.IdleWarning:
        this.dotnet.invokeMethodAsync("OnIdleWarning", JSON.parse(decoder.decode(body)).secondsLeft ?? 120);
        break;
      case Frame.End:
        this.finish(JSON.parse(decoder.decode(body)).reason ?? "The endpoint ended the session.");
        break;
      default:
        break;
    }
  }

  startViewer() {
    if (!this.viewer) {
      this.viewer = new Viewer(this, this.container, this.options);
      this.viewer.render();
    }
    this.viewer.setConnected();
  }

  // --- clipboard -------------------------------------------------------------

  clipboardEnabled() {
    return !!this.hello?.clipboard;
  }

  maxFileBytes() {
    return this.hello?.maxFileBytes || 0;
  }

  // sendClipboardText gives the endpoint the text of this computer's clipboard, unless the endpoint already holds it.
  sendClipboardText(text) {
    if (!this.clipboardEnabled() || typeof text !== "string" || text === "" || text === this.clipboardText) {
      return;
    }
    const bytes = encoder.encode(text);
    if (bytes.length > MaxClipboardBytes) {
      this.viewer?.notice("The text on your clipboard is larger than the 512 KB remote control synchronises. Use Type clipboard or a file instead.");
      return;
    }
    this.clipboardText = text;
    const frame = new Uint8Array(1 + bytes.length);
    frame[0] = Frame.Clipboard;
    frame.set(bytes, 1);
    this.sendFrame(frame).catch(() => {});
  }

  /** The signature of a set of files, to tell one paste from the next. */
  static signature(files) {
    return files.map((f) => `${f.name}:${f.size}`).join("|");
  }

  /** True when these files are the ones already on the endpoint clipboard: the paste shortcut belongs on the endpoint, not here. */
  filesAlreadyPlaced(files) {
    return this.placedFiles !== null && this.placedFiles === ControlSession.signature(files);
  }

  // onRemoteClipboard takes text copied on the endpoint. It goes on this computer's clipboard by itself only right after the technician
  // copied in this window; any other copy on the endpoint (the person there, a program) is offered with a button, so nobody at the
  // endpoint can put text on the technician's clipboard unasked (security review of 0.3.0 step 7). Without focus it waits for the next
  // focus or click.
  onRemoteClipboard(text) {
    // Something else is on the endpoint clipboard now, so files pasted earlier have to travel again.
    this.placedFiles = null;
    if (!this.clipboardEnabled() || encoder.encode(text).length > MaxClipboardBytes) {
      return;
    }
    this.clipboardText = text;
    if (this.viewer?.copiedRecently?.()) {
      this.pendingClipboard = text;
      this.flushRemoteClipboard();
      return;
    }
    this.offeredClipboard = text;
    this.viewer?.offerClipboardText?.();
  }

  // acceptRemoteClipboard puts the offered text on this computer's clipboard; called from the technician's click.
  acceptRemoteClipboard() {
    if (this.offeredClipboard === null) {
      return;
    }
    this.pendingClipboard = this.offeredClipboard;
    this.offeredClipboard = null;
    this.flushRemoteClipboard();
  }

  async flushRemoteClipboard() {
    const text = this.pendingClipboard;
    if (text === null || !navigator.clipboard?.writeText) {
      return;
    }
    try {
      await navigator.clipboard.writeText(text);
      if (this.pendingClipboard === text) {
        this.pendingClipboard = null;
      }
      this.viewer?.clipboardPending(false);
    } catch {
      this.viewer?.clipboardPending(true);
    }
  }

  // pasteFiles places files from this computer on the endpoint clipboard: they are uploaded to a folder only the signed-in user of the
  // endpoint can read, then pasted there with Ctrl+V. The folder is deleted when the session ends.
  async pasteFiles(files) {
    if (!this.clipboardEnabled()) {
      this.viewer?.notice("The clipboard is turned off for this endpoint by policy, so files cannot be pasted.");
      return;
    }
    const list = [...files];
    if (list.length === 0) {
      return;
    }
    const cap = this.maxFileBytes();
    const tooLarge = cap > 0 ? list.find((f) => f.size > cap) : null;
    if (tooLarge) {
      this.viewer?.notice(`${tooLarge.name} is larger than the ${Math.floor(cap / (1024 * 1024))} MB the policy allows for one file.`);
      return;
    }
    if (list.length > 100) {
      this.viewer?.notice("One paste can carry at most 100 files.");
      return;
    }
    this.viewer?.hint?.("paste");
    let batch;
    try {
      batch = (await this.request("clipboard.begin")).batch;
      for (const file of list) {
        const progress = this.viewer?.transfer(`Pasting ${file.name}`);
        try {
          await this.uploadWith("clipboard.upload", { batch }, file, (sent, total) => progress?.update(sent, total));
          progress?.done();
        } catch (error) {
          progress?.failed(error.message);
          throw error;
        }
      }
      const placed = await this.request("clipboard.place", { batch });
      this.placedFiles = ControlSession.signature(list);
      this.viewer?.notice(`${placed.count === 1 ? "1 file is" : placed.count + " files are"} on the endpoint clipboard. ` +
        "Press Ctrl+V on the endpoint, where you want them.");
    } catch (error) {
      if (error.message !== "cancelled") {
        this.viewer?.notice("The files could not be pasted: " + error.message);
      }
    }
  }

  // downloadCopied saves a file copied on the endpoint to this computer.
  async downloadCopied(index, name) {
    const progress = this.viewer?.transfer(`Downloading ${name}`);
    try {
      await this.downloadWith("clipboard.download", { index, offset: 0 }, name, (received, total) => progress?.update(received, total));
      progress?.done();
    } catch (error) {
      if (error.message === "cancelled") {
        progress?.remove();
      } else {
        progress?.failed(error.message);
      }
    }
  }

  // --- input -----------------------------------------------------------------

  // The technician's activity keeps the idle timer alive; the endpoint counts input frames.
  activity() {
    this.dotnet.invokeMethodAsync("OnActivity");
  }

  keepAlive() {
    this.sendFrame(jsonFrame(Frame.Activity, {})).catch(() => {});
  }

  setMonitor(monitor) {
    this.monitor = monitor;
    this.sendControl(Frame.Start, this.startBody(monitor));
  }

  /** The body of FrameStart: the monitor and the codecs this browser decodes, none once H.264 failed here. */
  startBody(monitor) {
    return { monitor, codecs: this.videoFailures >= maxVideoFailures ? [] : (this.codecs ?? []) };
  }

  // videoFailed handles a decoder that failed: the first time a new key frame is asked for, after that the screen continues as tiles.
  videoFailed(message) {
    this.videoFailures++;
    this.viewer?.video?.reset();
    if (this.videoFailures >= maxVideoFailures) {
      this.viewer?.notice(`This browser could not decode the H.264 video of the endpoint (${message}). The screen continues as tiles.`);
    }
    // A Start makes the endpoint send a whole frame: a key frame for a new decoder, or the first frame of tiles.
    this.sendControl(Frame.Start, this.startBody(this.monitor));
  }

  sendControl(type, body) {
    this.sendFrame(jsonFrame(type, body)).catch(() => {});
  }

  sendSecureAttention() {
    this.sendFrame(Uint8Array.of(Frame.SecureAttention)).catch(() => {});
  }

  sendFrame(plaintext) {
    if (!this.send || !this.socket || this.socket.readyState !== WebSocket.OPEN) {
      return Promise.reject(new Error("The session is not connected."));
    }
    const sealed = this.send.seal(plaintext);
    this.sendQueue = this.sendQueue.then(async () => {
      const bytes = await sealed;
      if (this.socket && this.socket.readyState === WebSocket.OPEN) {
        this.socket.send(bytes);
      }
    });
    return this.sendQueue;
  }

  // --- lifecycle -------------------------------------------------------------

  retryOrFail(message) {
    if (this.state === "failed" || this.state === "ended") {
      return;
    }
    this.closeSocket();
    this.closeTransfers();
    this.send = null;
    this.receive = null;
    if (this.reconnects >= 3) {
      this.fail(message + " Connect again to open a new session.");
      return;
    }
    this.reconnects++;
    this.state = "connecting";
    this.report("connecting", `${message} Reconnecting (${this.reconnects} of 3)…`);
    if (this.viewer) {
      this.viewer.releaseKeys();
    }
    const delay = 500 * this.reconnects;
    setTimeout(() => {
      if (this.state === "connecting") {
        this.open();
      }
    }, delay);
  }

  end(unloading) {
    if (this.state === "connected") {
      this.sendFrame(jsonFrame(Frame.End, { reason: "The technician ended the session." })).catch(() => {});
    }
    if (!unloading) {
      this.reconnects = 3; // an explicit end never reconnects
      this.finish("The session was ended.");
    }
  }

  report(state, message) {
    this.dotnet.invokeMethodAsync("OnState", state, message ?? null);
  }

  fail(message) {
    if (this.state === "failed" || this.state === "ended") {
      return;
    }
    this.state = "failed";
    this.close();
    this.report("failed", message);
  }

  finish(message) {
    if (this.state === "failed" || this.state === "ended") {
      return;
    }
    this.state = "ended";
    if (this.viewer) {
      this.viewer.disable();
    }
    setTimeout(() => this.close(), 250);
    this.report("ended", message);
  }

  closeSocket() {
    if (this.socket) {
      // A socket that is closed on purpose must not report its close as a lost connection: that would start a second reconnect.
      this.socket.onclose = null;
      this.socket.onmessage = null;
      if (this.socket.readyState <= WebSocket.OPEN) {
        this.socket.close(1000, "session ended");
      }
    }
    this.socket = null;
  }

  close() {
    window.removeEventListener("beforeunload", this.onUnload);
    window.removeEventListener("focus", this.onFocus);
    this.send = null;
    this.receive = null;
    this.browserKey = null;
    this.closeTransfers();
    this.closeSocket();
  }

  dispose() {
    this.end(true);
    this.state = "ended";
    if (this.viewer) {
      this.viewer.dispose();
      this.viewer = null;
    }
    this.close();
  }
}

installTransfers(ControlSession.prototype);

export function createSession(dotnet, container, options) {
  return new ControlSession(dotnet, container, options ?? {});
}
