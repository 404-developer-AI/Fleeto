// Request/response and file transfers over an encrypted remote session (0.3.0), shared by the Remote background window (remote.js: the file
// explorer) and the Remote control window (remote-control.js: files on the clipboard). The endpoint side is agent/internal/remote
// (background.go, transfers.go, clipboard.go). A session class installs these methods with installTransfers(Class.prototype) and provides
// state, sendFrame(plaintext), and the fields pending, nextRequestId, downloads, uploads and early.
import { Frame } from "./remote-crypto.mjs";

const encoder = new TextEncoder();

function jsonFrame(type, body) {
  return concat(Uint8Array.of(type), encoder.encode(JSON.stringify(body ?? {})));
}

function concat(a, b) {
  const out = new Uint8Array(a.length + b.length);
  out.set(a, 0);
  out.set(b, a.length);
  return out;
}

function jsonChunk(header, data) {
  return concat(Uint8Array.of(Frame.Chunk), concat(header, data));
}

/** The fields a session needs before installTransfers' methods are used. */
export function transferState() {
  return { pending: new Map(), nextRequestId: 1, downloads: new Map(), uploads: new Map(), early: new Map() };
}

const methods = {
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
  },

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
  },

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
  },

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
  },

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
  },

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
  },

  download(path, name, onProgress) {
    return this.downloadWith("download", { path, offset: 0 }, name, onProgress);
  },

  // downloadWith asks the endpoint to send a file with a request (op and params) and streams it to disk.
  async downloadWith(op, params, name, onProgress) {
    // The save location is chosen first, while the click still counts as a user gesture (the file picker requires one), and before
    // the endpoint starts sending.
    const sink = await createSink(name);
    let response;
    try {
      response = await this.request(op, params);
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
  },

  upload(path, file, onProgress) {
    return this.uploadWith("upload", { path }, file, onProgress);
  },

  // uploadWith asks the endpoint to take a file with a request (op and params; the name, size and offset are added) and streams it.
  async uploadWith(op, params, file, onProgress) {
    const response = await this.request(op, { ...params, name: file.name, size: file.size, offset: 0 });
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
  },

  cancelTransfer(transfer) {
    const d = this.downloads.get(transfer);
    if (d) {
      d.cancel();
    }
    const u = this.uploads.get(transfer);
    if (u) {
      u.cancel();
    }
  },

  sendTransfer(transfer, kind, bytes = 0) {
    this.sendFrame(jsonFrame(Frame.Transfer, { transfer, kind, bytes })).catch(() => {});
  },

  // closeTransfers rejects every open request and cancels every transfer when the session closes.
  closeTransfers() {
    for (const entry of this.pending.values()) {
      clearTimeout(entry.timer);
      entry.reject(new Error("The session ended."));
    }
    this.pending.clear();
    for (const t of [...this.downloads.values(), ...this.uploads.values()]) {
      try { t.cancel(); } catch { /* already gone */ }
    }
  }
};

/** Adds request/response and transfers to a session class. */
export function installTransfers(prototype) {
  for (const [name, method] of Object.entries(methods)) {
    prototype[name] = method;
  }
}

// createSink streams a download to disk with the File System Access API when it is available, or collects it and saves a Blob otherwise.
export async function createSink(name) {
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
