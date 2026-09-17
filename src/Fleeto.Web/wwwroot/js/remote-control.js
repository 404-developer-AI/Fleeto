// The browser side of a remote control session (0.3.0 step 3): the same key exchange and encrypted relay as remote background (remote.js),
// but the session carries the screen. The endpoint sends tiles (change detection, PNG for text and JPEG for the rest); this draws them on
// a canvas and sends the mouse and keyboard back. The session key never leaves this page; the gateway relays ciphertext it cannot read.
import { deriveSessionKeys, Frame, FrameCipher, fromBase64, generateBrowserKey, supported as cryptoSupported, toBase64 } from "./remote-crypto.mjs";
import { Viewer } from "./remote-control-ui.mjs";

const encoder = new TextEncoder();
const decoder = new TextDecoder();

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
    this.monitor = typeof options.monitor === "number" ? options.monitor : -2;
    // Auto reconnect (decided 2026-09-17): a dropped session opens a new one in this same window, up to a few tries.
    this.reconnects = 0;
    this.onUnload = () => this.end(true);
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
        this.browserKey = null;
      }
      return;
    }
    if (!this.receive) {
      throw new Error("The relay sent data before the session was set up.");
    }
    const plaintext = await this.receive.open(new Uint8Array(data));
    const type = plaintext[0];
    const body = plaintext.subarray(1);
    switch (type) {
      case Frame.Hello:
        this.hello = JSON.parse(decoder.decode(body));
        this.state = "connected";
        this.report("connected");
        this.startViewer();
        this.sendControl(Frame.Start, { monitor: this.monitor });
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
      case Frame.ControlNotice:
        if (this.viewer) {
          this.viewer.notice(JSON.parse(decoder.decode(body)).message ?? "");
        }
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

  // The technician's activity keeps the idle timer alive; the endpoint counts input frames.
  activity() {
    this.dotnet.invokeMethodAsync("OnActivity");
  }

  keepAlive() {
    this.sendFrame(jsonFrame(Frame.Activity, {})).catch(() => {});
  }

  setMonitor(monitor) {
    this.monitor = monitor;
    this.sendControl(Frame.Start, { monitor });
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

  retryOrFail(message) {
    if (this.state === "failed" || this.state === "ended") {
      return;
    }
    this.closeSocket();
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
    if (this.socket && this.socket.readyState <= WebSocket.OPEN) {
      this.socket.close(1000, "session ended");
    }
    this.socket = null;
  }

  close() {
    window.removeEventListener("beforeunload", this.onUnload);
    this.send = null;
    this.receive = null;
    this.browserKey = null;
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

export function createSession(dotnet, container, options) {
  return new ControlSession(dotnet, container, options ?? {});
}
