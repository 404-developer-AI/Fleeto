// Fleeto browser helpers. Loaded as a file from this origin (the Content Security Policy allows no inline script).
window.fleeto = {
  // The user's own time zone, so timestamps render in local time (branding §8).
  timeZone: function () {
    try {
      return Intl.DateTimeFormat().resolvedOptions().timeZone || "";
    } catch (e) {
      return "";
    }
  },
  copyText: function (text) {
    return navigator.clipboard.writeText(text);
  },
  // Opens a remote session in its own window (0.3.0); a window with the same name is reused and brought to the front. Same origin, so
  // the window keeps the sign-in. Returns false when the browser blocked it.
  openWindow: function (url, name) {
    var width = Math.min(1280, Math.max(800, Math.round(window.screen.availWidth * 0.7)));
    var height = Math.min(900, Math.max(600, Math.round(window.screen.availHeight * 0.75)));
    var opened = window.open(url, name, "popup=yes,width=" + width + ",height=" + height);
    if (!opened) {
      return false;
    }
    opened.focus();
    return true;
  },
  // Saves a small file produced by the server (a public certificate). The content arrives base64 over the circuit.
  saveFile: function (fileName, contentType, base64) {
    var bytes = Uint8Array.from(atob(base64), function (c) { return c.charCodeAt(0); });
    var url = URL.createObjectURL(new Blob([bytes], { type: contentType }));
    var link = document.createElement("a");
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
  },
  // Per-browser UI preferences (collapsed navigation, list height). Never account data or secrets.
  getPreference: function (key) {
    try {
      return window.localStorage.getItem(key);
    } catch (e) {
      return null;
    }
  },
  setPreference: function (key, value) {
    try {
      window.localStorage.setItem(key, value);
    } catch (e) {
      // Storage blocked: the preference is simply not remembered.
    }
  },
  // Submits a form rendered by the server (sign out: a POST with an antiforgery token).
  submitForm: function (id) {
    var form = document.getElementById(id);
    if (form) {
      form.requestSubmit ? form.requestSubmit() : form.submit();
    }
  },
  // Live connection dot and clock in the navigation. One timer per tab: the clock ticks every second in the browser's time
  // zone, and every 5 seconds the circuit is pinged; no answer within 4 seconds turns the dot red.
  startStatus: function (dotnetReference) {
    var fleeto = window.fleeto;
    fleeto._statusReference = dotnetReference;
    if (fleeto._statusTimer) {
      return;
    }

    var format = new Intl.DateTimeFormat("en-GB", {
      day: "2-digit", month: "2-digit", year: "numeric", hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false
    });
    var state = "ok";
    var seconds = 0;

    function paint() {
      var text = format.format(new Date()).replace(",", "");
      document.querySelectorAll("[data-fleeto-clock]").forEach(function (element) { element.textContent = text; });
      // Collapsed navigation: hours and minutes only ("dd/mm/yyyy hh:mm:ss" ends with the time).
      var shortText = text.slice(-8, -3);
      document.querySelectorAll("[data-fleeto-clock-short]").forEach(function (element) { element.textContent = shortText; });
      document.querySelectorAll("[data-fleeto-live]").forEach(function (element) {
        element.setAttribute("data-state", state);
        element.title = state === "ok"
          ? "Live: connected to the server"
          : "Not connected to the server. Live changes return when the connection is back.";
      });
    }

    function ping() {
      var answered = false;
      var timeout = setTimeout(function () {
        if (!answered) { state = "lost"; paint(); }
      }, 4000);
      fleeto._statusReference.invokeMethodAsync("Ping").then(function () {
        answered = true; clearTimeout(timeout); state = "ok"; paint();
      }, function () {
        answered = true; clearTimeout(timeout); state = "lost"; paint();
      });
    }

    paint();
    fleeto._statusTimer = setInterval(function () {
      seconds++;
      if (seconds % 5 === 0) {
        ping();
      }
      paint();
    }, 1000);
  },
  // Drag handle that resizes the maximum height of a scrollable list. Wires an element once; the height is remembered.
  initSplitter: function (handle, target, storageKey) {
    if (!handle || !target || handle.dataset.fleetoSplitter === "1") {
      return;
    }

    handle.dataset.fleetoSplitter = "1";
    var saved = window.fleeto.getPreference(storageKey);
    if (saved && /^\d+px$/.test(saved)) {
      target.style.maxHeight = saved;
    }

    handle.addEventListener("pointerdown", function (down) {
      down.preventDefault();
      handle.setPointerCapture(down.pointerId);
      handle.classList.add("dragging");
      var startY = down.clientY;
      var startHeight = target.getBoundingClientRect().height;

      function move(event) {
        var height = Math.round(Math.max(160, Math.min(window.innerHeight * 0.85, startHeight + event.clientY - startY)));
        target.style.maxHeight = height + "px";
      }

      function up(event) {
        handle.releasePointerCapture(event.pointerId);
        handle.classList.remove("dragging");
        handle.removeEventListener("pointermove", move);
        handle.removeEventListener("pointerup", up);
        window.fleeto.setPreference(storageKey, target.style.maxHeight);
      }

      handle.addEventListener("pointermove", move);
      handle.addEventListener("pointerup", up);
    });
  },
  // The storage summary of a drive as an image (0.7.0): drawn on a canvas in the branding of Fleeto, so it looks the same in every
  // mail or ticket it is pasted into. The server sends the text already formatted; nothing here reads the page. Copies a PNG to the
  // clipboard and answers "copied"; where the browser refuses that, it downloads the PNG and answers "downloaded".
  copyStorageImage: async function (report) {
    var canvas = await window.fleeto.drawStorageImage(report);
    var blob = await new Promise(function (resolve) { canvas.toBlob(resolve, "image/png"); });
    try {
      await navigator.clipboard.write([new ClipboardItem({ "image/png": blob })]);
      return "copied";
    } catch (e) {
      window.fleeto.saveBlob(report.fileName, blob);
      return "downloaded";
    }
  },
  saveBlob: function (fileName, blob) {
    var url = URL.createObjectURL(blob);
    var link = document.createElement("a");
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
  },
  drawStorageImage: async function (report) {
    var colors = {
      primary: "#0F766E", tint: "#F0FDFA", tintStrong: "#CCFBF1", text: "#1C1917", secondary: "#78716C", muted: "#A8A29E",
      border: "#E7E5E4", surface: "#FAFAF9", warning: "#B45309", white: "#FFFFFF"
    };
    var font = "Inter, -apple-system, \"Segoe UI\", Roboto, sans-serif";
    try {
      await Promise.all([document.fonts.load("400 13px Inter"), document.fonts.load("600 13px Inter")]);
    } catch (e) {
      // The system font is drawn instead.
    }

    var width = 760, pad = 28, row = 26, scale = 2;
    var height = pad + 54 + 64 + 104 + (report.warning ? 24 : 0) + 2 * (32 + row + 12) +
      row * (Math.max(1, report.folders.length) + Math.max(1, report.files.length)) + 40;
    var canvas = document.createElement("canvas");
    canvas.width = width * scale;
    canvas.height = height * scale;
    var g = canvas.getContext("2d");
    g.scale(scale, scale);
    g.textBaseline = "alphabetic";

    function setFont(size, weight) { g.font = (weight || 400) + " " + size + "px " + font; }
    function text(value, x, y, size, color, weight, align) {
      setFont(size, weight);
      g.fillStyle = color;
      g.textAlign = align || "left";
      g.fillText(value, x, y);
    }
    function rounded(x, y, w, h, r, color) {
      if (w <= 0 || h <= 0) { return; }
      r = Math.min(r, w / 2, h / 2);
      g.beginPath();
      g.moveTo(x + r, y);
      g.arcTo(x + w, y, x + w, y + h, r);
      g.arcTo(x + w, y + h, x, y + h, r);
      g.arcTo(x, y + h, x, y, r);
      g.arcTo(x, y, x + w, y, r);
      g.closePath();
      g.fillStyle = color;
      g.fill();
    }
    // Shortens with an ellipsis in the middle, so a path keeps its drive and its last folder or file name.
    function fit(value, maxWidth, size, weight) {
      setFont(size, weight);
      if (g.measureText(value).width <= maxWidth) { return value; }
      var head = Math.floor(value.length / 2), tail = value.length - head;
      while (head + tail > 0) {
        if (tail >= head && tail > 0) { tail--; } else { head--; }
        var candidate = value.slice(0, head) + "…" + value.slice(value.length - tail);
        if (g.measureText(candidate).width <= maxWidth) { return candidate; }
      }
      return "…";
    }

    g.fillStyle = colors.white;
    g.fillRect(0, 0, width, height);
    var right = width - pad, y = pad;

    // Header: the mark of Fleeto, a teal rounded square with a white heartbeat line (branding section 5).
    rounded(pad, y, 28, 28, 7, colors.primary);
    g.strokeStyle = colors.white;
    g.lineWidth = 2;
    g.lineJoin = "round";
    g.lineCap = "round";
    g.beginPath();
    [[5, 15], [9.5, 15], [12, 9], [16, 21], [18.5, 13], [20.5, 15], [23.5, 15]].forEach(function (p, i) {
      if (i === 0) { g.moveTo(pad + p[0], y + p[1]); } else { g.lineTo(pad + p[0], y + p[1]); }
    });
    g.stroke();
    text("Fleeto", pad + 38, y + 20, 17, colors.text, 600);
    text("Storage report", right, y + 20, 13, colors.secondary, 400, "right");
    y += 40;
    g.fillStyle = colors.border;
    g.fillRect(pad, y, width - 2 * pad, 1);
    y += 14;

    text(fit(report.hostname, width - 2 * pad, 22, 600), pad, y + 22, 22, colors.text, 600);
    text(fit(report.owner, width - 2 * pad, 13), pad, y + 42, 13, colors.secondary);
    y += 64;

    // The drive.
    rounded(pad, y, width - 2 * pad, 92, 12, colors.surface);
    text(report.drive, pad + 18, y + 28, 15, colors.text, 600);
    text(report.percentFull, right - 18, y + 28, 15, colors.text, 600, "right");
    rounded(pad + 18, y + 40, width - 2 * pad - 36, 10, 5, colors.tintStrong);
    rounded(pad + 18, y + 40, (width - 2 * pad - 36) * Math.max(0, Math.min(1, report.usedShare)), 10, 5, colors.primary);
    text(report.usage, pad + 18, y + 74, 13, colors.text);
    text(report.scanned, right - 18, y + 74, 12, colors.secondary, 400, "right");
    y += 104;
    if (report.warning) {
      text(fit(report.warning, width - 2 * pad, 12), pad, y + 4, 12, colors.warning);
      y += 24;
    }

    function table(title, columns, rows, drawRow) {
      text(title, pad, y + 20, 15, colors.text, 600);
      y += 32;
      rounded(pad, y, width - 2 * pad, row, 6, colors.tint);
      columns.forEach(function (c) { text(c.title, c.x, y + 17, 11, colors.primary, 600, c.align); });
      y += row;
      rows.forEach(function (r, i) {
        if (i % 2 === 1) { g.fillStyle = colors.surface; g.fillRect(pad, y, width - 2 * pad, row); }
        drawRow(r, y + 17);
        g.fillStyle = colors.border;
        g.fillRect(pad, y + row - 1, width - 2 * pad, 1);
        y += row;
      });
      if (rows.length === 0) {
        text(report.empty, pad + 10, y + 17, 12, colors.secondary);
        y += row;
      }
      y += 12;
    }

    table(report.foldersTitle, [
      { title: "Folder", x: pad + 10 }, { title: "Size on disk", x: pad + 470, align: "right" }, { title: "Of the drive", x: pad + 490 }
    ], report.folders, function (f, baseline) {
      text(fit(f.path, 380, 13), pad + 10, baseline, 13, colors.text);
      text(f.size, pad + 470, baseline, 13, colors.text, 500, "right");
      rounded(pad + 490, baseline - 9, 130, 8, 4, colors.tint);
      rounded(pad + 490, baseline - 9, 130 * Math.max(0, Math.min(1, f.share)), 8, 4, colors.primary);
      text(f.percent, right - 10, baseline, 12, colors.secondary, 400, "right");
    });

    table(report.filesTitle, [
      { title: "File", x: pad + 10 }, { title: "Size on disk", x: pad + 540, align: "right" }, { title: "Changed", x: right - 10, align: "right" }
    ], report.files, function (f, baseline) {
      text(fit(f.path, 440, 13), pad + 10, baseline, 13, colors.text);
      text(f.size, pad + 540, baseline, 13, colors.text, 500, "right");
      text(f.changed, right - 10, baseline, 12, colors.secondary, 400, "right");
    });

    text(fit(report.footer, width - 2 * pad, 11), pad, height - 18, 11, colors.muted);
    return canvas;
  }
};

// Copy buttons on static pages (no circuit): <button data-copy-target="elementId">.
document.addEventListener("click", function (event) {
  var button = event.target.closest ? event.target.closest("[data-copy-target]") : null;
  if (!button) {
    return;
  }

  var target = document.getElementById(button.getAttribute("data-copy-target"));
  if (!target) {
    return;
  }

  navigator.clipboard.writeText(target.innerText.trim()).then(function () {
    var original = button.textContent;
    button.textContent = "Copied";
    setTimeout(function () { button.textContent = original; }, 2000);
  });
});
