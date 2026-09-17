// The browser side of a remote background session (0.3.0): key exchange, the encrypted relay connection, the terminal, and the file,
// service and process workspace. Loaded as a module by the Remote background window. The session key never leaves this page; the server
// only signs its public half. The workspace UI is in remote-ui.mjs.
import { deriveSessionKeys, Frame, FrameCipher, fromBase64, generateBrowserKey, supported as cryptoSupported, toBase64 } from "./remote-crypto.mjs";
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
    this.pending = new Map();
    this.nextRequestId = 1;
    this.downloads = new Map();
    this.uploads = new Map();
    // Transfer frames that arrive before their transfer is registered (the endpoint starts sending as soon as it answers the request),
    // kept per transfer id until download() or upload() claims them.
    this.early = new Map();
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

  // --- request/response ------------------------------------------------------

  request(op, params = {}) {
    if (this.state !== "connected") {
      return Promise.reject(new Error("The session is not connected."));
    }
    const id = "r" + this.nextRequestId++;
    const payload = { id, op, ...params };
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error("The endpoint did not answer in time."));
      }, 60000);
      this.pending.set(id, { resolve, reject, timer });
      this.sendFrame(jsonFrame(Frame.Request, payload)).catch((error) => {
        clearTimeout(timer);
        this.pending.delete(id);
        reject(error);
      });
    });
  }

  resolveRequest(message) {
    const entry = this.pending.get(message.id);
    if (!entry) {
      return;
    }
    clearTimeout(entry.timer);
    this.pending.delete(message.id);
    if (message.ok) {
      entry.resolve(message);
    } else {
      entry.reject(new Error(message.error || "The action failed."));
    }
  }

  // --- transfers -------------------------------------------------------------

  async onChunk(body) {
    if (body.length < 4) {
      return;
    }
    const transfer = new DataView(body.buffer, body.byteOffset, 4).getUint32(0);
    const download = this.downloads.get(transfer);
    if (download) {
      await download.onChunk(body.subarray(4));
    } else if (!this.uploads.has(transfer)) {
      this.keepEarly(transfer, { chunk: body.slice(4) });
    }
  }

  async onTransfer(message) {
    const download = this.downloads.get(message.transfer);
    if (download) {
      await download.onControl(message);
      return;
    }
    const upload = this.uploads.get(message.transfer);
    if (upload) {
      upload.onControl(message);
    } else {
      this.keepEarly(message.transfer, { control: message });
    }
  }

  keepEarly(transfer, frame) {
    const entry = this.early.get(transfer) ?? { frames: [], bytes: 0 };
    entry.frames.push(frame);
    entry.bytes += frame.chunk ? frame.chunk.length : 0;
    // The endpoint never runs more than its flow-control window (4 MiB) ahead of acknowledgements, so this stays small; anything beyond
    // that is not a transfer this page asked for.
    if (entry.bytes > 8 * 1024 * 1024) {
      this.early.delete(transfer);
      return;
    }
    this.early.set(transfer, entry);
  }

  // registerTransfer replays the frames that arrived before the transfer was known, in order, and then stores its state. Frames that
  // arrive while the replay writes are kept too and replayed in the next round; the state is stored only when nothing is waiting, with
  // no await in between, so a later frame can never overtake an earlier one.
  async registerTransfer(map, transfer, state) {
    for (;;) {
      const entry = this.early.get(transfer);
      if (!entry) {
        if (!state.finished) {
          map.set(transfer, state);
        }
        return;
      }
      this.early.delete(transfer);
      for (const frame of entry.frames) {
        if (frame.chunk && state.onChunk) {
          await state.onChunk(frame.chunk);
        } else if (frame.control) {
          await state.onControl(frame.control);
        }
      }
    }
  }

  async download(path, name, onProgress) {
    // The save location is chosen first, while the click still counts as a user gesture (the file picker requires one), and before
    // the endpoint starts sending.
    const sink = await createSink(name);
    let response;
    try {
      response = await this.request("download", { path, offset: 0 });
    } catch (error) {
      await sink.abort();
      throw error;
    }
    const transfer = response.transfer;
    return new Promise((resolve, reject) => {
      let received = 0;
      let acked = 0;
      const state = {
        onChunk: async (data) => {
          try {
            await sink.write(data);
          } catch (error) {
            this.sendTransfer(transfer, "cancel");
            cleanup();
            reject(error);
            return;
          }
          received += data.length;
          if (received - acked >= 2 * 1024 * 1024 || received === response.size) {
            acked = received;
            this.sendTransfer(transfer, "ack", received);
          }
          if (onProgress) {
            onProgress(received, response.size);
          }
        },
        onControl: async (message) => {
          if (message.kind === "end") {
            await sink.close();
            cleanup();
            resolve({ size: received });
          } else if (message.kind === "error") {
            await sink.abort();
            cleanup();
            reject(new Error(message.error || "The download failed."));
          }
        },
        cancel: () => {
          this.sendTransfer(transfer, "cancel");
          sink.abort();
          cleanup();
          reject(new Error("cancelled"));
        }
      };
      const cleanup = () => {
        state.finished = true;
        this.downloads.delete(transfer);
      };
      this.registerTransfer(this.downloads, transfer, state).catch((error) => {
        cleanup();
        reject(error);
      });
    });
  }

  async upload(path, file, onProgress) {
    const response = await this.request("upload", { path, name: file.name, size: file.size, offset: 0 });
    const transfer = response.transfer;
    const chunkSize = 256 * 1024;
    const windowBytes = 4 * 1024 * 1024;
    return new Promise((resolve, reject) => {
      let sent = response.resumeOffset || 0;
      let acked = sent;
      let waiter = null;
      const state = {
        onControl: (message) => {
          if (message.kind === "ack") {
            acked = message.bytes;
            if (waiter) {
              const w = waiter;
              waiter = null;
              w();
            }
          } else if (message.kind === "end") {
            cleanup();
            resolve({ size: sent });
          } else if (message.kind === "error") {
            cleanup();
            reject(new Error(message.error || "The upload failed."));
          }
        },
        cancel: () => {
          this.sendTransfer(transfer, "cancel");
          cleanup();
          reject(new Error("cancelled"));
        }
      };
      const cleanup = () => {
        state.finished = true;
        this.uploads.delete(transfer);
      };
      this.registerTransfer(this.uploads, transfer, state);

      (async () => {
        try {
          while (sent < file.size) {
            while (sent - acked > windowBytes) {
              await new Promise((r) => { waiter = r; });
            }
            const slice = file.slice(sent, Math.min(sent + chunkSize, file.size));
            const bytes = new Uint8Array(await slice.arrayBuffer());
            const header = new Uint8Array(4);
            new DataView(header.buffer).setUint32(0, transfer);
            await this.sendFrame(jsonChunk(header, bytes));
            sent += bytes.length;
            if (onProgress) {
              onProgress(sent, file.size);
            }
          }
          this.sendTransfer(transfer, "end");
        } catch (error) {
          this.sendTransfer(transfer, "cancel");
          cleanup();
          reject(error);
        }
      })();
    });
  }

  cancelTransfer(transfer) {
    const d = this.downloads.get(transfer);
    if (d) {
      d.cancel();
    }
    const u = this.uploads.get(transfer);
    if (u) {
      u.cancel();
    }
  }

  sendTransfer(transfer, kind, bytes = 0) {
    this.sendFrame(jsonFrame(Frame.Transfer, { transfer, kind, bytes })).catch(() => {});
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
    for (const entry of this.pending.values()) {
      clearTimeout(entry.timer);
      entry.reject(new Error("The session ended."));
    }
    this.pending.clear();
    for (const t of [...this.downloads.values(), ...this.uploads.values()]) {
      try { t.cancel(); } catch { /* already gone */ }
    }
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

function jsonChunk(header, data) {
  return concat(Uint8Array.of(Frame.Chunk), concat(header, data));
}

// createSink streams a download to disk with the File System Access API when it is available, or collects it and saves a Blob otherwise.
async function createSink(name) {
  if (window.showSaveFilePicker) {
    try {
      const handle = await window.showSaveFilePicker({ suggestedName: name });
      const writable = await handle.createWritable();
      return {
        write: (data) => writable.write(data),
        close: () => writable.close(),
        abort: () => writable.abort().catch(() => {})
      };
    } catch (error) {
      if (error && error.name === "AbortError") {
        throw new Error("cancelled");
      }
      // Fall through to the Blob sink.
    }
  }
  const parts = [];
  return {
    write: (data) => { parts.push(data.slice()); return Promise.resolve(); },
    close: () => {
      const url = URL.createObjectURL(new Blob(parts));
      const link = document.createElement("a");
      link.href = url;
      link.download = name || "download";
      document.body.appendChild(link);
      link.click();
      link.remove();
      setTimeout(() => URL.revokeObjectURL(url), 2000);
      parts.length = 0;
      return Promise.resolve();
    },
    abort: () => { parts.length = 0; return Promise.resolve(); }
  };
}

export function createSession(dotnet, container, options) {
  return new RemoteSession(dotnet, container, options ?? {});
}
