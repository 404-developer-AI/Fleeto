// The workspace of a remote background session (0.3.0 step 2): the tab bar and the Terminal, Files, Services and Processes panels. It talks
// to the endpoint through the RemoteSession (remote.js): session.request(op, params) for file, service and process operations,
// session.download / session.upload for transfers, and the terminal frames for the shell. It builds plain DOM; nothing here is trusted
// with more than the technician already has on the endpoint.
import { Terminal } from "../lib/xterm/xterm.mjs";
import { FitAddon } from "../lib/xterm/addon-fit.mjs";
import { Frame } from "./remote-crypto.mjs";

const decoder = new TextDecoder();

export class Workspace {
  constructor(session, root, hello, options) {
    this.session = session;
    this.root = root;
    this.hello = hello;
    this.options = options;
    this.windows = hello.platform === "windows";
    this.sep = this.windows ? "\\" : "/";
    this.tabs = {};
    this.panels = {};
    this.active = "terminal";
    this.terminal = null;
    this.channel = 0;
    this.clipboard = null; // {path, name} for copy/paste within the endpoint
    this.disabled = false;
  }

  render() {
    this.root.replaceChildren();
    const bar = el("div", "remote-tabs");
    const body = el("div", "remote-panels");
    const definitions = [
      ["terminal", "Terminal"],
      ["files", "Files"],
      ["services", "Services"],
      ["processes", "Processes"]
    ];
    for (const [key, label] of definitions) {
      const tab = el("button", "remote-tab");
      tab.type = "button";
      tab.textContent = label;
      tab.addEventListener("click", () => this.select(key));
      this.tabs[key] = tab;
      bar.appendChild(tab);
      const panel = el("div", "remote-panel");
      this.panels[key] = panel;
      body.appendChild(panel);
    }
    this.root.append(bar, body);
    this.buildTerminal();
    this.buildFiles();
    this.buildServices();
    this.buildProcesses();
    this.select("terminal");
  }

  select(key) {
    this.active = key;
    for (const [name, tab] of Object.entries(this.tabs)) {
      tab.classList.toggle("active", name === key);
      this.panels[name].classList.toggle("active", name === key);
    }
    if (key === "terminal") {
      if (!this.terminal) {
        this.openTerminal();
      }
      setTimeout(() => this.fit(), 0);
    } else if (key === "files" && !this.filesLoaded) {
      this.filesLoaded = true;
      this.goHome();
    } else if (key === "services" && !this.servicesLoaded) {
      this.servicesLoaded = true;
      this.refreshServices();
    } else if (key === "processes" && !this.processesLoaded) {
      this.processesLoaded = true;
      this.refreshProcesses();
    }
  }

  onFrame(type, body) {
    switch (type) {
      case Frame.Opened: {
        const opened = JSON.parse(decoder.decode(body));
        if (opened.channel !== this.channel || !this.terminal) {
          return;
        }
        if (opened.error) {
          this.terminal.write(`\r\n${opened.error}\r\n`);
          return;
        }
        this.pty = opened.pty !== false;
        this.terminal.options.convertEol = !this.pty;
        if (!this.pty) {
          this.terminal.write("This endpoint has no pseudo console (Windows Server 2016): type a command and press Enter.\r\n");
        }
        this.fit();
        this.terminal.focus();
        break;
      }
      case Frame.Data:
        if (this.terminal && ((body[0] << 8) | body[1]) === this.channel) {
          this.terminal.write(body.subarray(2));
        }
        break;
      case Frame.Closed: {
        const closed = JSON.parse(decoder.decode(body));
        if (closed.channel === this.channel && this.terminal) {
          const code = typeof closed.exitCode === "number" ? ` with exit code ${closed.exitCode}` : "";
          this.terminal.write(`\r\n\r\nThe shell ended${code}.${closed.error ? " " + closed.error : ""}\r\n`);
          this.channel = 0;
          this.showRestart();
        }
        break;
      }
      default:
        break;
    }
  }

  // --- terminal --------------------------------------------------------------

  buildTerminal() {
    const panel = this.panels.terminal;
    const surface = el("div", "remote-terminal-surface");
    panel.appendChild(surface);
    this.terminalSurface = surface;
    this.terminal = new Terminal({
      cursorBlink: true,
      fontFamily: "Consolas, 'Cascadia Mono', 'DejaVu Sans Mono', monospace",
      fontSize: 14,
      scrollback: 10000,
      theme: { background: "#0b1220", foreground: "#e2e8f0", cursor: "#5eead4" }
    });
    this.fitAddon = new FitAddon();
    this.terminal.loadAddon(this.fitAddon);
    this.terminal.open(surface);
    this.line = "";
    this.terminal.onData((data) => this.input(data));
    this.terminal.onResize(({ cols, rows }) => {
      if (this.channel) {
        this.session.resizeTerminalChannel(this.channel, cols, rows);
      }
    });
    window.addEventListener("resize", () => this.fit());
    this.openTerminal();
  }

  openTerminal() {
    if (this.disabled) {
      return;
    }
    this.channel = (this.channel || this.lastChannel || 0) + 1;
    this.lastChannel = this.channel;
    this.line = "";
    this.removeRestart();
    this.fit();
    const shell = this.options.shell || (this.hello.shells && this.hello.shells[0]) || "";
    this.session.openTerminalChannel(this.channel, shell, this.terminal.cols, this.terminal.rows);
  }

  showRestart() {
    this.removeRestart();
    const button = el("button", "remote-restart");
    button.type = "button";
    button.textContent = "Start a new terminal";
    button.addEventListener("click", () => this.openTerminal());
    this.terminalSurface.appendChild(button);
    this.restartButton = button;
  }

  removeRestart() {
    if (this.restartButton) {
      this.restartButton.remove();
      this.restartButton = null;
    }
  }

  input(data) {
    if (!this.channel || this.disabled) {
      return;
    }
    this.session.activity();
    if (this.pty) {
      this.session.sendTerminalData(this.channel, new TextEncoder().encode(data));
      return;
    }
    if (data.startsWith("")) {
      return;
    }
    for (const ch of data) {
      if (ch === "\r") {
        this.terminal.write("\r\n");
        this.session.sendTerminalData(this.channel, new TextEncoder().encode(this.line + "\r"));
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

  fit() {
    try {
      this.fitAddon?.fit();
    } catch {
      // Not visible yet.
    }
  }

  // --- files -----------------------------------------------------------------

  buildFiles() {
    const panel = this.panels.files;
    const toolbar = el("div", "remote-toolbar");
    this.pathLabel = el("div", "remote-path");
    const up = button("Up", () => this.navigate(this.parent));
    const refresh = button("Refresh", () => this.listFiles(this.path));
    const mkdir = button("New folder", () => this.newFolder());
    const upload = button("Upload", () => this.fileInput.click());
    this.pasteButton = button("Paste", () => this.paste());
    this.pasteButton.disabled = true;
    this.fileInput = el("input");
    this.fileInput.type = "file";
    this.fileInput.style.display = "none";
    this.fileInput.addEventListener("change", () => this.uploadChosen());
    toolbar.append(up, refresh, mkdir, upload, this.pasteButton, this.fileInput);
    this.transfers = el("div", "remote-transfers");
    this.fileError = el("div", "remote-error");
    this.fileTable = el("div", "remote-table");
    panel.append(toolbar, this.pathLabel, this.fileError, this.transfers, this.fileTable);
  }

  async goHome() {
    try {
      const home = await this.session.request("home");
      this.roots = home.roots || [];
      this.navigate(home.path);
    } catch (error) {
      this.fileError.textContent = error.message;
    }
  }

  navigate(path) {
    if (!path) {
      if (this.roots && this.roots.length > 1) {
        this.showRoots();
        return;
      }
      path = this.roots ? this.roots[0] : this.path;
    }
    this.listFiles(path);
  }

  showRoots() {
    this.path = "";
    this.parent = "";
    this.pathLabel.textContent = "This PC";
    this.fileError.textContent = "";
    this.pasteButton.disabled = !this.clipboard;
    this.fileTable.replaceChildren();
    for (const root of this.roots) {
      const row = el("div", "remote-row remote-dir");
      row.append(el("span", "remote-name", "💾 " + root));
      row.addEventListener("dblclick", () => this.navigate(root));
      this.fileTable.appendChild(row);
    }
  }

  async listFiles(path) {
    this.fileError.textContent = "Loading…";
    try {
      const result = await this.session.request("list", { path });
      this.path = result.path;
      this.parent = result.parent;
      this.pathLabel.textContent = result.path;
      this.pasteButton.disabled = !this.clipboard;
      this.fileError.textContent = result.truncated ? "Showing the first files of a large folder." : "";
      this.renderFiles(result.entries);
    } catch (error) {
      this.fileError.textContent = error.message;
    }
  }

  renderFiles(entries) {
    this.fileTable.replaceChildren();
    const header = el("div", "remote-row remote-head");
    header.append(el("span", "remote-name", "Name"), el("span", "remote-size", "Size"), el("span", "remote-modified", "Modified"), el("span", "remote-actions", ""));
    this.fileTable.appendChild(header);
    for (const entry of entries) {
      const row = el("div", "remote-row" + (entry.dir ? " remote-dir" : ""));
      const icon = entry.dir ? "📁" : "📄";
      const name = el("span", "remote-name", `${icon} ${entry.name}`);
      row.append(name, el("span", "remote-size", entry.dir ? "" : formatBytes(entry.size)), el("span", "remote-modified", formatDate(entry.modified)));
      const actions = el("span", "remote-actions");
      if (!entry.dir) {
        actions.appendChild(iconButton("Download", () => this.download(entry)));
      }
      actions.appendChild(iconButton("Copy", () => this.copy(entry)));
      actions.appendChild(iconButton("Rename", () => this.rename(entry)));
      actions.appendChild(iconButton("Delete", () => this.remove(entry)));
      row.appendChild(actions);
      if (entry.dir) {
        row.addEventListener("dblclick", () => this.navigate(this.join(entry.name)));
      }
      this.fileTable.appendChild(row);
    }
  }

  join(name) {
    return this.path.endsWith(this.sep) ? this.path + name : this.path + this.sep + name;
  }

  async newFolder() {
    const name = prompt("Name of the new folder:");
    if (!name) {
      return;
    }
    await this.run(() => this.session.request("mkdir", { path: this.path, name }), "The folder could not be created");
    this.listFiles(this.path);
  }

  async rename(entry) {
    const name = prompt("New name:", entry.name);
    if (!name || name === entry.name) {
      return;
    }
    await this.run(() => this.session.request("rename", { path: this.join(entry.name), name }), "It could not be renamed");
    this.listFiles(this.path);
  }

  async remove(entry) {
    if (!confirm(`Delete ${entry.name}?${entry.dir ? " The folder and everything in it will be deleted." : ""}`)) {
      return;
    }
    await this.run(() => this.session.request("delete", { path: this.join(entry.name), recursive: entry.dir }), "It could not be deleted");
    this.listFiles(this.path);
  }

  copy(entry) {
    this.clipboard = { path: this.join(entry.name), name: entry.name };
    this.pasteButton.disabled = false;
    this.fileError.textContent = `Copied ${entry.name}. Open a folder and choose Paste.`;
  }

  async paste() {
    if (!this.clipboard) {
      return;
    }
    await this.run(() => this.session.request("copy", { path: this.clipboard.path, dest: this.path }), "It could not be copied");
    this.listFiles(this.path);
  }

  async download(entry) {
    const transfer = this.addTransfer(`Downloading ${entry.name}`);
    try {
      await this.session.download(this.join(entry.name), entry.name, (received, total) => transfer.progress(received, total));
      transfer.done("Downloaded " + entry.name);
    } catch (error) {
      transfer.fail(error.message === "cancelled" ? "Download cancelled" : "Download failed: " + error.message);
    }
  }

  uploadChosen() {
    const file = this.fileInput.files && this.fileInput.files[0];
    this.fileInput.value = "";
    if (!file) {
      return;
    }
    if (file.size > (this.hello.maxFileBytes || 0)) {
      this.fileError.textContent = `This file is larger than the ${Math.round((this.hello.maxFileBytes || 0) / (1024 * 1024))} MB a transfer may carry.`;
      return;
    }
    this.doUpload(file);
  }

  async doUpload(file) {
    const transfer = this.addTransfer(`Uploading ${file.name}`);
    try {
      await this.session.upload(this.path, file, (sent, total) => transfer.progress(sent, total));
      transfer.done("Uploaded " + file.name);
      this.listFiles(this.path);
    } catch (error) {
      transfer.fail(error.message === "cancelled" ? "Upload cancelled" : "Upload failed: " + error.message);
    }
  }

  addTransfer(label) {
    const row = el("div", "remote-transfer");
    const text = el("span", "remote-transfer-label", label);
    const bar = el("div", "remote-progress");
    const fill = el("div", "remote-progress-fill");
    bar.appendChild(fill);
    row.append(text, bar);
    this.transfers.appendChild(row);
    return {
      progress: (value, total) => {
        const pct = total > 0 ? Math.round((value / total) * 100) : 0;
        fill.style.width = pct + "%";
        text.textContent = `${label} — ${pct}%`;
      },
      done: (message) => {
        text.textContent = message;
        fill.style.width = "100%";
        setTimeout(() => row.remove(), 4000);
      },
      fail: (message) => {
        text.textContent = message;
        row.classList.add("failed");
        setTimeout(() => row.remove(), 8000);
      }
    };
  }

  // --- services --------------------------------------------------------------

  buildServices() {
    const panel = this.panels.services;
    const toolbar = el("div", "remote-toolbar");
    toolbar.appendChild(button("Refresh", () => this.refreshServices()));
    this.serviceFilter = el("input", "remote-filter");
    this.serviceFilter.placeholder = "Filter services";
    this.serviceFilter.addEventListener("input", () => this.renderServices());
    toolbar.appendChild(this.serviceFilter);
    this.serviceError = el("div", "remote-error");
    this.serviceTable = el("div", "remote-table");
    panel.append(toolbar, this.serviceError, this.serviceTable);
  }

  async refreshServices() {
    this.serviceError.textContent = "Loading…";
    try {
      const result = await this.session.request("services");
      this.serviceList = result.services || [];
      this.serviceError.textContent = "";
      this.renderServices();
    } catch (error) {
      this.serviceError.textContent = error.message;
    }
  }

  renderServices() {
    const filter = (this.serviceFilter.value || "").toLowerCase();
    this.serviceTable.replaceChildren();
    const header = el("div", "remote-row remote-head");
    header.append(el("span", "remote-svc-name", "Service"), el("span", "remote-svc-state", "State"), el("span", "remote-svc-start", "Start type"), el("span", "remote-actions", ""));
    this.serviceTable.appendChild(header);
    for (const svc of this.serviceList) {
      const label = (svc.displayName || svc.name).toLowerCase();
      if (filter && !label.includes(filter) && !svc.name.toLowerCase().includes(filter)) {
        continue;
      }
      const row = el("div", "remote-row");
      const name = el("span", "remote-svc-name");
      name.append(el("span", "remote-svc-display", svc.displayName || svc.name), el("span", "muted", svc.name));
      row.append(name, el("span", "remote-svc-state " + stateClass(svc.state), svc.state || "—"), el("span", "remote-svc-start", startTypeLabel(svc.startType)));
      const actions = el("span", "remote-actions");
      if (svc.state === "running") {
        actions.append(iconButton("Stop", () => this.serviceAction(svc, "stop")), iconButton("Restart", () => this.serviceAction(svc, "restart")));
      } else {
        actions.append(iconButton("Start", () => this.serviceAction(svc, "start")));
      }
      const select = el("select", "remote-startselect");
      for (const [value, text] of this.startTypes()) {
        const option = el("option");
        option.value = value;
        option.textContent = text;
        option.selected = value === svc.startType;
        select.appendChild(option);
      }
      select.addEventListener("change", () => this.serviceAction(svc, "start_type", select.value));
      actions.appendChild(select);
      row.appendChild(actions);
      this.serviceTable.appendChild(row);
    }
  }

  startTypes() {
    if (this.windows) {
      return [["automatic", "Automatic"], ["automatic_delayed", "Automatic (delayed)"], ["manual", "Manual"], ["disabled", "Disabled"]];
    }
    return [["automatic", "Enabled"], ["manual", "Disabled"], ["disabled", "Masked"]];
  }

  async serviceAction(svc, action, startType) {
    this.serviceError.textContent = `${action.replace("_", " ")} ${svc.displayName || svc.name}…`;
    try {
      await this.session.request("service", { name: svc.name, action, startType });
      await this.refreshServices();
    } catch (error) {
      this.serviceError.textContent = error.message;
    }
  }

  // --- processes -------------------------------------------------------------

  buildProcesses() {
    const panel = this.panels.processes;
    const toolbar = el("div", "remote-toolbar");
    toolbar.appendChild(button("Refresh", () => this.refreshProcesses()));
    this.processFilter = el("input", "remote-filter");
    this.processFilter.placeholder = "Filter processes";
    this.processFilter.addEventListener("input", () => this.renderProcesses());
    toolbar.appendChild(this.processFilter);
    this.processError = el("div", "remote-error");
    this.processTable = el("div", "remote-table");
    panel.append(toolbar, this.processError, this.processTable);
  }

  async refreshProcesses() {
    this.processError.textContent = "Sampling…";
    try {
      const result = await this.session.request("processes");
      this.processList = result.processes || [];
      this.processError.textContent = "";
      this.renderProcesses();
    } catch (error) {
      this.processError.textContent = error.message;
    }
  }

  renderProcesses() {
    const filter = (this.processFilter.value || "").toLowerCase();
    this.processTable.replaceChildren();
    const header = el("div", "remote-row remote-head");
    header.append(el("span", "remote-proc-name", "Name"), el("span", "remote-proc-pid", "PID"), el("span", "remote-proc-user", "User"),
      el("span", "remote-proc-cpu", "CPU"), el("span", "remote-proc-mem", "Memory"), el("span", "remote-actions", ""));
    this.processTable.appendChild(header);
    for (const proc of this.processList) {
      if (filter && !proc.name.toLowerCase().includes(filter) && !(proc.user || "").toLowerCase().includes(filter)) {
        continue;
      }
      const row = el("div", "remote-row");
      row.append(el("span", "remote-proc-name", proc.name), el("span", "remote-proc-pid", String(proc.pid)), el("span", "remote-proc-user", proc.user || "—"),
        el("span", "remote-proc-cpu", proc.cpu.toFixed(1) + "%"), el("span", "remote-proc-mem", formatBytes(proc.memory)));
      const actions = el("span", "remote-actions");
      actions.appendChild(iconButton("End", () => this.endProcess(proc)));
      row.appendChild(actions);
      this.processTable.appendChild(row);
    }
  }

  async endProcess(proc) {
    if (!confirm(`End ${proc.name} (pid ${proc.pid})? Unsaved work in it is lost.`)) {
      return;
    }
    try {
      await this.session.request("process", { pid: proc.pid, action: "end" });
      await this.refreshProcesses();
    } catch (error) {
      this.processError.textContent = error.message;
    }
  }

  // --- helpers ---------------------------------------------------------------

  async run(action, failMessage) {
    try {
      await action();
      this.fileError.textContent = "";
    } catch (error) {
      this.fileError.textContent = `${failMessage}: ${error.message}`;
    }
  }

  disable() {
    this.disabled = true;
    for (const button of this.root.querySelectorAll("button, select, input")) {
      button.disabled = true;
    }
  }

  dispose() {
    this.disabled = true;
    if (this.terminal) {
      this.terminal.dispose();
      this.terminal = null;
    }
  }
}

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) {
    node.className = className;
  }
  if (text !== undefined) {
    node.textContent = text;
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

function iconButton(label, onClick) {
  const node = button(label, onClick);
  node.classList.add("remote-icon-button");
  return node;
}

function formatBytes(bytes) {
  if (!bytes) {
    return "0 B";
  }
  const units = ["B", "KB", "MB", "GB", "TB"];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value.toFixed(unit === 0 ? 0 : 1)} ${units[unit]}`;
}

function formatDate(ms) {
  if (!ms) {
    return "";
  }
  return new Date(ms).toLocaleString();
}

function stateClass(state) {
  if (state === "running") {
    return "ok";
  }
  if (state === "stopped") {
    return "off";
  }
  return "pending";
}

function startTypeLabel(type) {
  switch (type) {
    case "automatic": return "Automatic";
    case "automatic_delayed": return "Automatic (delayed)";
    case "manual": return "Manual";
    case "disabled": return "Disabled";
    default: return type || "—";
  }
}
