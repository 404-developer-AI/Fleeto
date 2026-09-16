// The browser side of a remote background session (0.3.0): key exchange, the encrypted relay connection and the terminal. Loaded as a
// module by the Remote background window. The session key never leaves this page; the server only signs its public half.
import { Terminal } from "../lib/xterm/xterm.mjs";
import { FitAddon } from "../lib/xterm/addon-fit.mjs";
import { deriveSessionKeys, Frame, FrameCipher, fromBase64, generateBrowserKey, supported as cryptoSupported, toBase64 } from "./remote-crypto.mjs";

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

function frame(type, body) {
  const bytes = body instanceof Uint8Array ? body : encoder.encode(JSON.stringify(body ?? {}));
  const out = new Uint8Array(bytes.length + 1);
  out[0] = type;
  out.set(bytes, 1);
  return out;
}

function dataFrame(channel, bytes) {
  const out = new Uint8Array(bytes.length + 3);
  out[0] = Frame.Data;
  out[1] = channel >> 8;
  out[2] = channel & 0xff;
  out.set(bytes, 3);
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
    this.channel = 0;
    this.pty = true;
    this.line = "";
    this.state = "idle";
    this.queue = Promise.resolve();
    this.onUnload = () => this.end(true);
    this.onResize = () => this.fit();
  }

  async start() {
    this.report("connecting");
    this.createTerminal();
    this.write("Setting up an encrypted session...\r\n");
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
      this.write("Waiting for the endpoint...\r\n");
    };
    socket.onmessage = (event) => {
      // In order: every frame is decrypted with the next counter value.
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
    window.addEventListener("resize", this.onResize);
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
    const body = plaintext.subarray(1);
    switch (plaintext[0]) {
      case Frame.Hello:
        this.hello = JSON.parse(decoder.decode(body));
        this.state = "connected";
        this.report("connected");
        this.terminal.clear();
        await this.openTerminal();
        break;
      case Frame.Opened: {
        const opened = JSON.parse(decoder.decode(body));
        if (opened.channel !== this.channel) {
          break;
        }
        if (opened.error) {
          this.write(`\r\n${opened.error}\r\n`);
          this.dotnet.invokeMethodAsync("OnTerminalClosed");
          break;
        }
        this.pty = opened.pty !== false;
        this.terminal.options.convertEol = !this.pty;
        if (!this.pty) {
          this.write("This endpoint has no pseudo console (Windows Server 2016): type a command and press Enter. Full-screen programs do not work here.\r\n");
        }
        this.fit();
        this.terminal.focus();
        break;
      }
      case Frame.Data:
        if (((body[0] << 8) | body[1]) === this.channel) {
          this.terminal.write(body.subarray(2));
        }
        break;
      case Frame.Closed: {
        const closed = JSON.parse(decoder.decode(body));
        if (closed.channel === this.channel) {
          const code = typeof closed.exitCode === "number" ? ` with exit code ${closed.exitCode}` : "";
          this.write(`\r\n\r\nThe shell ended${code}.${closed.error ? " " + closed.error : ""}\r\n`);
          this.channel = 0;
          this.dotnet.invokeMethodAsync("OnTerminalClosed");
        }
        break;
      }
      case Frame.IdleWarning:
        this.dotnet.invokeMethodAsync("OnIdleWarning", JSON.parse(decoder.decode(body)).secondsLeft ?? 120);
        break;
      case Frame.End:
        this.finish(JSON.parse(decoder.decode(body)).reason ?? "The endpoint ended the session.");
        break;
      default:
        // A newer endpoint may send frames this page does not know.
        break;
    }
  }

  createTerminal() {
    if (this.terminal) {
      this.terminal.dispose();
    }
    this.container.replaceChildren();
    this.terminal = new Terminal({
      cursorBlink: true,
      fontFamily: "Consolas, 'Cascadia Mono', 'DejaVu Sans Mono', monospace",
      fontSize: 14,
      scrollback: 10000,
      theme: { background: "#0b1220", foreground: "#e2e8f0", cursor: "#5eead4" }
    });
    this.fitAddon = new FitAddon();
    this.terminal.loadAddon(this.fitAddon);
    this.terminal.open(this.container);
    this.fit();
    this.terminal.onData((data) => this.input(data));
    this.terminal.onResize(({ cols, rows }) => {
      if (this.channel && this.state === "connected") {
        this.sendFrame(frame(Frame.Resize, { channel: this.channel, cols, rows }));
      }
    });
  }

  async openTerminal() {
    if (this.state !== "connected") {
      return;
    }
    this.channel = (this.channel || this.lastChannel || 0) + 1;
    this.lastChannel = this.channel;
    const shells = this.hello?.shells ?? [];
    const shell = this.options.shell && shells.includes(this.options.shell) ? this.options.shell : (shells[0] ?? this.options.shell ?? "");
    this.line = "";
    this.fit();
    await this.sendFrame(frame(Frame.Open, { channel: this.channel, service: "terminal", shell, cols: this.terminal.cols, rows: this.terminal.rows }));
  }

  input(data) {
    if (!this.channel || this.state !== "connected") {
      return;
    }
    this.dotnet.invokeMethodAsync("OnActivity");
    if (this.pty) {
      this.sendFrame(dataFrame(this.channel, encoder.encode(data)));
      return;
    }
    // Line input without a pseudo console: edit here, send with Enter.
    if (data.startsWith("")) {
      return;
    }
    for (const ch of data) {
      if (ch === "\r") {
        this.terminal.write("\r\n");
        this.sendFrame(dataFrame(this.channel, encoder.encode(this.line + "\r")));
        this.line = "";
      } else if (ch === "" || ch === "\b") {
        if (this.line.length > 0) {
          this.line = Array.from(this.line).slice(0, -1).join("");
          this.terminal.write("\b \b");
        }
      } else if (ch >= " ") {
        this.line += ch;
        this.terminal.write(ch);
      }
    }
  }

  async sendFrame(plaintext) {
    if (!this.send || !this.socket || this.socket.readyState !== WebSocket.OPEN) {
      return;
    }
    // Sealed in call order, sent in the same order.
    const sealed = this.send.seal(plaintext);
    this.sendQueue = (this.sendQueue ?? Promise.resolve()).then(async () => {
      const bytes = await sealed;
      if (this.socket && this.socket.readyState === WebSocket.OPEN) {
        this.socket.send(bytes);
      }
    });
    return this.sendQueue;
  }

  keepAlive() {
    this.sendFrame(frame(Frame.Activity, {}));
  }

  end(unloading) {
    if (this.state === "connected") {
      this.sendFrame(frame(Frame.End, { reason: "The technician ended the session." }));
    }
    if (unloading) {
      return;
    }
    this.finish("The session was ended.");
  }

  fit() {
    try {
      this.fitAddon?.fit();
    } catch {
      // Not visible yet.
    }
  }

  write(text) {
    this.terminal?.write(text);
  }

  report(state, message) {
    this.dotnet.invokeMethodAsync("OnState", state, message ?? null);
  }

  fail(message) {
    if (this.state === "failed" || this.state === "ended") {
      return;
    }
    this.state = "failed";
    this.write(`\r\n${message}\r\n`);
    this.close();
    this.report("failed", message);
  }

  finish(message) {
    if (this.state === "failed" || this.state === "ended") {
      return;
    }
    this.state = "ended";
    this.write(`\r\n\r\n${message}\r\n`);
    // Give a queued End frame a moment to leave before closing.
    setTimeout(() => this.close(), 250);
    this.report("ended", message);
  }

  close() {
    window.removeEventListener("beforeunload", this.onUnload);
    window.removeEventListener("resize", this.onResize);
    this.send = null;
    this.receive = null;
    this.browserKey = null;
    if (this.socket && this.socket.readyState <= WebSocket.OPEN) {
      this.socket.close(1000, "session ended");
    }
  }

  dispose() {
    this.end(true);
    this.state = "ended";
    this.close();
    this.terminal?.dispose();
    this.terminal = null;
  }
}

export function createSession(dotnet, container, options) {
  return new RemoteSession(dotnet, container, options ?? {});
}
