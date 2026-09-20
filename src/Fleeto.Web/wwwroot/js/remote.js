// The browser side of a remote background session (0.3.0): key exchange, the encrypted relay connection, the terminal, and the file,
// service and process workspace. Loaded as a module by the Remote background window. The session key never leaves this page; the server
// only signs its public half. The workspace UI is in remote-ui.mjs.
import { deriveSessionKeys, Frame, FrameCipher, fromBase64, generateBrowserKey, supported as cryptoSupported, toBase64, wipeKeys } from "./remote-crypto.mjs";
import { installTransfers, transferState } from "./remote-transfers.mjs";
import { Workspace } from "./remote-ui.mjs";

const encoder = new TextEncoder();
const decoder = new TextDecoder();

export function supported() {
  return typeof WebSocket === "function" && cryptoSupported();
}

function relayUrl(configured, participantId) {
  if (configured) {
    return configured.replace(/\/?$/, "/") + participantId;
  }
  const scheme = window.location.protocol === "https:" ? "wss:" : "ws:";
  return `${scheme}//${window.location.host}/relay/v1/sessions/${participantId}`;
}

function jsonFrame(type, body) {
  return concat(Uint8Array.of(type), encoder.encode(JSON.stringify(body ?? {})));
}

function concat(a, b) {
  const out = new Uint8Array(a.length + b.length);
  out.set(a, 0);
  out.set(b, a.length);
  return out;
}

class RemoteSession {
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
    this.hello = null;
    this.workspace = null;
    // Requests and transfers (remote-transfers.mjs). Transfer frames that arrive before their transfer is registered (the endpoint starts
    // sending as soon as it answers the request) are kept per transfer id until download() or upload() claims them.
    Object.assign(this, transferState());
    this.onUnload = () => this.end(true);
  }

  // --- lifecycle -------------------------------------------------------------

  async start() {
    this.report("connecting");
    this.container.replaceChildren();
    const status = document.createElement("div");
    status.className = "remote-connecting";
    status.textContent = "Setting up an encrypted session…";
    this.container.appendChild(status);
    this.statusLine = status;
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
      this.setStatus("Waiting for the endpoint…");
    };
    socket.onmessage = (event) => {
      this.queue = this.queue.then(() => this.onMessage(event.data)).catch((error) => this.fail(error?.message ?? String(error)));
    };
    socket.onclose = (event) => {
      if (this.state === "connecting") {
        this.fail(event.reason ? `The relay closed the connection (${event.reason}).` : "The relay closed the connection before the session started.");
      } else if (this.state === "connected") {
        this.finish("The connection to the endpoint was lost. Connect again to open a new session.");
      }
    };
    window.addEventListener("beforeunload", this.onUnload);
  }

  async onMessage(data) {
    if (typeof data === "string") {
      const message = JSON.parse(data);
      if (message.type === "error") {
        this.fail(message.message);
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
    const type = plaintext[0];
    const body = plaintext.subarray(1);
    switch (type) {
      case Frame.Hello:
        this.hello = JSON.parse(decoder.decode(body));
        this.state = "connected";
        this.report("connected");
        this.startWorkspace();
        break;
      case Frame.IdleWarning:
        this.dotnet.invokeMethodAsync("OnIdleWarning", JSON.parse(decoder.decode(body)).secondsLeft ?? 120);
        break;
      case Frame.End:
        this.finish(JSON.parse(decoder.decode(body)).reason ?? "The endpoint ended the session.");
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
      default:
        if (this.workspace) {
          this.workspace.onFrame(type, body, decoder);
        }
        break;
    }
  }

  startWorkspace() {
    this.container.replaceChildren();
    this.workspace = new Workspace(this, this.container, this.hello, this.options);
    this.workspace.render();
  }

  // --- terminal (used by the workspace) --------------------------------------

  openTerminalChannel(channel, shell, cols, rows) {
    this.sendFrame(jsonFrame(Frame.Open, { channel, service: "terminal", shell, cols, rows })).catch(() => {});
  }

  sendTerminalData(channel, bytes) {
    const header = new Uint8Array(2);
    new DataView(header.buffer).setUint16(0, channel);
    this.sendFrame(concat(Uint8Array.of(Frame.Data), concat(header, bytes))).catch(() => {});
  }

  resizeTerminalChannel(channel, cols, rows) {
    this.sendFrame(jsonFrame(Frame.Resize, { channel, cols, rows })).catch(() => {});
  }

  closeTerminalChannel(channel) {
    this.sendFrame(jsonFrame(Frame.CloseChannel, { channel })).catch(() => {});
  }

  activity() {
    this.dotnet.invokeMethodAsync("OnActivity");
  }

  keepAlive() {
    this.sendFrame(jsonFrame(Frame.Activity, {})).catch(() => {});
  }

  // --- sending ---------------------------------------------------------------

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

  // --- ending ----------------------------------------------------------------

  end(unloading) {
    if (this.state === "connected") {
      this.sendFrame(jsonFrame(Frame.End, { reason: "The technician ended the session." })).catch(() => {});
    }
    if (!unloading) {
      this.finish("The session was ended.");
    }
  }

  setStatus(text) {
    if (this.statusLine) {
      this.statusLine.textContent = text;
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
    if (this.workspace) {
      this.workspace.disable();
    }
    setTimeout(() => this.close(), 250);
    this.report("ended", message);
  }

  close() {
    window.removeEventListener("beforeunload", this.onUnload);
    this.send = null;
    this.receive = null;
    this.browserKey = null;
    this.closeTransfers();
    if (this.socket && this.socket.readyState <= WebSocket.OPEN) {
      this.socket.close(1000, "session ended");
    }
  }

  dispose() {
    this.end(true);
    this.state = "ended";
    if (this.workspace) {
      this.workspace.dispose();
      this.workspace = null;
    }
    this.close();
  }
}

installTransfers(RemoteSession.prototype);

export function createSession(dotnet, container, options) {
  return new RemoteSession(dotnet, container, options ?? {});
}
