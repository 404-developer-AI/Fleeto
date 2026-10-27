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
  // Starts Fleeto Connect (0.8.0) with its link. Only the fleeto-connect: scheme is followed; the system asks the user to open the program.
  openConnectLink: function (link) {
    if (typeof link !== "string" || link.indexOf("fleeto-connect://") !== 0) {
      return false;
    }
    window.location.href = link;
    return true;
  },
  // The operating system of this PC for the download of Fleeto Connect: "windows", "ubuntu" (any Linux) or "".
  connectPlatform: function () {
    var platform = ((navigator.userAgentData && navigator.userAgentData.platform) || navigator.platform || navigator.userAgent || "").toLowerCase();
    if (platform.indexOf("win") >= 0) {
      return "windows";
    }
    return platform.indexOf("linux") >= 0 && platform.indexOf("android") < 0 ? "ubuntu" : "";
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
      var sent = performance.now();
      var timeout = setTimeout(function () {
        if (!answered) { state = "lost"; paint(); }
      }, 4000);
      fleeto._statusReference.invokeMethodAsync("Ping").then(function () {
        answered = true; clearTimeout(timeout); state = "ok"; paint();
        // The round trip of the live connection, for the performance monitor when this page reports.
        if (fleeto._vitals && document.visibilityState === "visible") {
          fleeto._vitals.add("circuit", performance.now() - sent, null);
        }
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
  // Web Vitals for the performance monitor (0.7.0 step 3). The server decides once per page load whether this page reports; while
  // Diagnose runs a page that does not report asks every minute whether it should start, and then measures without its page load.
  // Measured: largest and first contentful paint, time to first byte and connection setup of the page load; per page view the layout
  // shift and the slowest interaction; long tasks; the round trip of the ping above. A view ends when the path changes or the tab is
  // hidden. Only the path of the page is sent, never its query or content; reports go over the circuit every 30 seconds.
  startVitals: function (dotnetReference, sampled) {
    var fleeto = window.fleeto;
    if (fleeto._vitalsStarted) {
      fleeto._vitalsReference = dotnetReference;
      return;
    }

    fleeto._vitalsStarted = true;
    fleeto._vitalsReference = dotnetReference;
    var queue = [];
    var view = null;

    function round(value) { return Math.round(value * 1000) / 1000; }

    function add(metric, value, path) {
      if (queue.length < 60 && isFinite(value) && value >= 0) {
        queue.push({ path: path || window.location.pathname, metric: metric, value: round(value) });
      }
    }

    function observe(type, callback, options) {
      try {
        var observer = new PerformanceObserver(function (list) { list.getEntries().forEach(callback); });
        var init = { type: type, buffered: true };
        if (options) {
          Object.keys(options).forEach(function (key) { init[key] = options[key]; });
        }
        observer.observe(init);
      } catch (e) {
        // Not supported by this browser: the metric is simply not reported.
      }
    }

    // The view of the current path: its layout shift as the largest session window (gaps under 1 s, at most 5 s), and its slowest
    // interaction. Reported when it ends.
    function newView(path) {
      view = { path: path, cls: 0, windowValue: 0, windowStart: 0, windowLast: 0, inp: 0 };
      add("view", 0, path);
    }

    function endView() {
      if (!view) {
        return;
      }
      add("cls", view.cls, view.path);
      if (view.inp > 0) {
        add("inp", view.inp, view.path);
      }
      view = null;
    }

    function current() {
      var path = window.location.pathname;
      if (!view || view.path !== path) {
        endView();
        newView(path);
      }
      return view;
    }

    function send() {
      if (queue.length === 0 || !fleeto._vitalsReference) {
        return;
      }
      var batch = queue.splice(0, queue.length);
      fleeto._vitalsReference.invokeMethodAsync("ReportVitals", batch).catch(function () {
        // A lost report is a lost sample, nothing more.
      });
    }

    function begin(withLoad) {
      var loadPath = window.location.pathname;
      var lcp = null;
      var lcpReported = !withLoad;
      newView(loadPath);
      fleeto._vitals = { add: add };

      if (withLoad) {
        var navigation = performance.getEntriesByType ? performance.getEntriesByType("navigation")[0] : null;
        if (navigation) {
          if (navigation.responseStart > 0) {
            add("ttfb", navigation.responseStart, loadPath);
          }
          var connect = navigation.connectEnd - navigation.domainLookupStart;
          if (navigation.connectEnd > 0 && connect > 0) {
            add("connect", connect, null);
          }
        }
        observe("paint", function (entry) {
          if (entry.name === "first-contentful-paint") {
            add("fcp", entry.startTime, loadPath);
          }
        });
        observe("largest-contentful-paint", function (entry) { lcp = entry.startTime; });
      }

      // The largest contentful paint is final at the first input or when the tab is hidden.
      function reportLcp() {
        if (!lcpReported && lcp !== null) {
          add("lcp", lcp, loadPath);
        }
        lcpReported = true;
      }
      ["keydown", "pointerdown"].forEach(function (type) {
        window.addEventListener(type, reportLcp, { once: true, capture: true });
      });

      observe("layout-shift", function (entry) {
        if (entry.hadRecentInput) {
          return;
        }
        var v = current();
        if (v.windowValue > 0 && (entry.startTime - v.windowLast > 1000 || entry.startTime - v.windowStart > 5000)) {
          v.windowValue = 0;
        }
        if (v.windowValue === 0) {
          v.windowStart = entry.startTime;
        }
        v.windowValue += entry.value;
        v.windowLast = entry.startTime;
        v.cls = Math.max(v.cls, v.windowValue);
      });
      observe("event", function (entry) {
        if (entry.interactionId) {
          var v = current();
          v.inp = Math.max(v.inp, entry.duration);
        }
      }, { durationThreshold: 40 });
      observe("longtask", function (entry) { add("long-task", entry.duration, current().path); });

      setInterval(current, 2000);
      setInterval(send, 30000);
      document.addEventListener("visibilitychange", function () {
        if (document.visibilityState === "hidden") {
          reportLcp();
          endView();
          send();
        } else {
          current();
        }
      });
    }

    if (sampled) {
      begin(true);
      return;
    }

    var asking = setInterval(function () {
      if (document.visibilityState !== "visible" || !fleeto._vitalsReference) {
        return;
      }
      fleeto._vitalsReference.invokeMethodAsync("VitalsWanted").then(function (wanted) {
        if (wanted && !fleeto._vitals) {
          clearInterval(asking);
          begin(false);
        }
      }, function () {
        // No answer: ask again next minute.
      });
    }, 60000);
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
  // The storage summary of a drive and the process list of a CPU or memory result as an image (0.7.0): drawn on a canvas in the
  // branding of Fleeto, so it looks the same in every mail or ticket it is pasted into. The server sends the text already formatted;
  // nothing here reads the page. Copies a PNG to the clipboard and answers "copied"; where the browser refuses that, it downloads the
  // PNG and answers "downloaded".
  copyStorageImage: async function (report) {
    return window.fleeto.copyCanvas(await window.fleeto.drawStorageImage(report), report.fileName);
  },
  copyProcessImage: async function (report) {
    return window.fleeto.copyCanvas(await window.fleeto.drawProcessImage(report), report.fileName);
  },
  copyCanvas: async function (canvas, fileName) {
    var blob = await new Promise(function (resolve) { canvas.toBlob(resolve, "image/png"); });
    try {
      await navigator.clipboard.write([new ClipboardItem({ "image/png": blob })]);
      return "copied";
    } catch (e) {
      window.fleeto.saveBlob(fileName, blob);
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
  // A white canvas with the drawing helpers of a report image and its header: the mark of Fleeto, a teal rounded square with a
  // white heartbeat line (branding section 5), the name and the title. Answers the helpers and the y below the header.
  reportPainter: async function (width, height, pad, title) {
    var colors = {
      primary: "#0F766E", tint: "#F0FDFA", tintStrong: "#CCFBF1", text: "#1C1917", secondary: "#78716C", muted: "#A8A29E",
      border: "#E7E5E4", surface: "#FAFAF9", warning: "#B45309", error: "#DC2626", white: "#FFFFFF"
    };
    var font = "Inter, -apple-system, \"Segoe UI\", Roboto, sans-serif";
    try {
      await Promise.all([document.fonts.load("400 13px Inter"), document.fonts.load("600 13px Inter")]);
    } catch (e) {
      // The system font is drawn instead.
    }

    var scale = 2;
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
    var y = pad;
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
    text(title, width - pad, y + 20, 13, colors.secondary, 400, "right");
    y += 40;
    g.fillStyle = colors.border;
    g.fillRect(pad, y, width - 2 * pad, 1);
    return { canvas: canvas, g: g, colors: colors, text: text, rounded: rounded, fit: fit, y: y + 14 };
  },
  drawStorageImage: async function (report) {
    var width = 760, pad = 28, row = 26;
    var height = pad + 54 + 64 + 104 + (report.warning ? 24 : 0) + 2 * (32 + row + 12) +
      row * (Math.max(1, report.folders.length) + Math.max(1, report.files.length)) + 40;
    var p = await window.fleeto.reportPainter(width, height, pad, "Storage report");
    var canvas = p.canvas, g = p.g, colors = p.colors, text = p.text, rounded = p.rounded, fit = p.fit;
    var right = width - pad, y = p.y;

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
  },
  drawProcessImage: async function (report) {
    var width = 760, pad = 28, row = 26;
    var chart = report.chart, chartScale = (width - 2 * pad) / chart.width, chartHeight = chart.height * chartScale;
    var height = pad + 54 + 64 + 92 + 32 + chartHeight + 22 + 12 + 32 + row + row * Math.max(1, report.processes.length) + 12 + 40;
    var p = await window.fleeto.reportPainter(width, height, pad, report.title);
    var canvas = p.canvas, g = p.g, colors = p.colors, text = p.text, rounded = p.rounded, fit = p.fit;
    var right = width - pad, y = p.y;

    text(fit(report.hostname, width - 2 * pad, 22, 600), pad, y + 22, 22, colors.text, 600);
    text(fit(report.owner, width - 2 * pad, 13), pad, y + 42, 13, colors.secondary);
    y += 64;

    // The measurement.
    rounded(pad, y, width - 2 * pad, 80, 12, colors.surface);
    text(fit(report.check, width - 2 * pad - 200, 15, 600), pad + 18, y + 30, 15, colors.text, 600);
    text(report.value, right - 18, y + 32, 22, colors.text, 600, "right");
    text(report.measured, pad + 18, y + 60, 13, colors.text);
    if (report.thresholds) {
      text(report.thresholds, right - 18, y + 60, 12, colors.secondary, 400, "right");
    }
    y += 92;

    // The chart of the period as the check history draws it, in its own coordinates scaled to the width of the image; the dot of
    // the copied process list is filled. Lines and texts are drawn at their own size, not scaled.
    text(chart.title, pad, y + 20, 15, colors.text, 600);
    y += 32;
    var top = y;
    function cx(x) { return pad + x * chartScale; }
    function cy(v) { return top + v * chartScale; }
    var plotLeft = cx(chart.left), plotRight = cx(chart.width - chart.right);
    function line(atY, color, dashed) {
      g.save();
      g.strokeStyle = color;
      g.lineWidth = 1;
      g.setLineDash(dashed ? [6, 4] : []);
      g.beginPath();
      g.moveTo(plotLeft, atY);
      g.lineTo(plotRight, atY);
      g.stroke();
      g.restore();
    }
    chart.yTicks.forEach(function (t) {
      line(cy(t.at), colors.border, false);
      text(t.label, plotLeft - 6, cy(t.at) + 4, 11, colors.secondary, 400, "right");
    });
    chart.xTicks.forEach(function (t) { text(t.label, cx(t.at), top + chartHeight - 8, 11, colors.secondary, 400, "center"); });
    function path(data, draw) {
      if (!data) { return; }
      g.save();
      g.translate(pad, top);
      g.scale(chartScale, chartScale);
      draw(new Path2D(data));
      g.restore();
    }
    path(chart.band, function (shape) { g.globalAlpha = 0.7; g.fillStyle = colors.tintStrong; g.fill(shape); });
    path(chart.average, function (shape) {
      g.strokeStyle = colors.primary;
      g.lineWidth = 2 / chartScale;
      g.lineJoin = "round";
      g.stroke(shape);
    });
    if (chart.warningY !== null) { line(cy(chart.warningY), "#D97706", true); }
    if (chart.criticalY !== null) { line(cy(chart.criticalY), colors.error, true); }
    chart.problems.forEach(function (s) {
      g.fillStyle = colors.error;
      g.fillRect(cx(s.x), top + (chart.top + chart.plotHeight + 2) * chartScale, Math.max(1, s.width * chartScale), 4);
    });
    chart.markers.forEach(function (m) {
      g.beginPath();
      g.arc(cx(m.x), cy(m.y), m.selected ? 5 : 3, 0, 2 * Math.PI);
      g.fillStyle = m.selected ? colors.primary : colors.white;
      g.fill();
      g.strokeStyle = colors.primary;
      g.lineWidth = 2;
      g.stroke();
    });
    y = top + chartHeight + 4;

    // The legend, as under the check history.
    var legendX = plotLeft;
    function legend(label, draw) {
      draw(legendX, y + 10);
      text(label, legendX + 22, y + 14, 11, colors.secondary);
      legendX += 22 + g.measureText(label).width + 18;
    }
    legend("Average", function (x, mid) { g.fillStyle = colors.primary; g.fillRect(x, mid - 1, 16, 2); });
    legend("Lowest to highest", function (x, mid) { rounded(x, mid - 4, 16, 8, 2, colors.tintStrong); });
    if (chart.warningY !== null) {
      legend("Warning threshold", function (x, mid) { g.fillStyle = "#D97706"; g.fillRect(x, mid - 1, 6, 2); g.fillRect(x + 10, mid - 1, 6, 2); });
    }
    if (chart.criticalY !== null) {
      legend("Critical threshold", function (x, mid) { g.fillStyle = colors.error; g.fillRect(x, mid - 1, 6, 2); g.fillRect(x + 10, mid - 1, 6, 2); });
    }
    legend("Process list", function (x, mid) {
      g.beginPath();
      g.arc(x + 8, mid, 3, 0, 2 * Math.PI);
      g.fillStyle = colors.white;
      g.fill();
      g.strokeStyle = colors.primary;
      g.lineWidth = 2;
      g.stroke();
    });
    legend("Copied list", function (x, mid) {
      g.beginPath();
      g.arc(x + 8, mid, 4, 0, 2 * Math.PI);
      g.fillStyle = colors.primary;
      g.fill();
    });
    y += 22 + 12;

    text(report.processesTitle, pad, y + 20, 15, colors.text, 600);
    y += 32;
    // Columns from the right: memory, then CPU with a bar of its share of the machine when the list has it.
    // Process names are short; users such as NT AUTHORITY\NETWORK SERVICE get the room that is left.
    var memoryX = right - 10, cpuX = report.cpu ? right - 110 : null, barX = report.cpu ? right - 240 : null;
    var pidX = pad + 225, userX = pidX + 20, userEnd = report.cpu ? barX - 14 : memoryX - 120;
    var columns = [{ title: "Process", x: pad + 10 }, { title: "PID", x: pidX, align: "right" }, { title: "User", x: userX }];
    if (report.cpu) { columns.push({ title: "CPU", x: barX }); }
    columns.push({ title: "Memory", x: memoryX, align: "right" });
    rounded(pad, y, width - 2 * pad, row, 6, colors.tint);
    columns.forEach(function (c) { text(c.title, c.x, y + 17, 11, colors.primary, 600, c.align); });
    y += row;
    report.processes.forEach(function (process, i) {
      var baseline = y + 17;
      if (i % 2 === 1) { g.fillStyle = colors.surface; g.fillRect(pad, y, width - 2 * pad, row); }
      text(fit(process.name, pidX - 45 - pad - 10, 13), pad + 10, baseline, 13, colors.text);
      text(process.pid, pidX, baseline, 12, colors.secondary, 400, "right");
      text(fit(process.user, userEnd - userX, 12), userX, baseline, 12, colors.secondary);
      if (report.cpu) {
        rounded(barX, baseline - 9, 70, 8, 4, colors.tint);
        rounded(barX, baseline - 9, 70 * Math.max(0, Math.min(1, process.cpuShare)), 8, 4, colors.primary);
        text(process.cpu, cpuX, baseline, 13, colors.text, 500, "right");
      }
      text(process.memory, memoryX, baseline, 13, colors.text, 500, "right");
      g.fillStyle = colors.border;
      g.fillRect(pad, y + row - 1, width - 2 * pad, 1);
      y += row;
    });
    if (report.processes.length === 0) {
      text(report.empty, pad + 10, y + 17, 12, colors.secondary);
    }

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

// A table wider than its box scrolls sideways with a scrollbar under its last row, so on a long list the user had to scroll all
// the way down first. While that scrollbar is out of view, a copy of it sticks to the bottom of the visible part of the table
// (the window, or the box the table scrolls in) and moves the table with it. Works for every table without changes to a page.
(function () {
  var bar = null;
  var inner = null;
  var target = null;
  var syncing = false;
  var scheduled = false;

  function clips(element) {
    var style = window.getComputedStyle(element);
    return style.overflowX !== "visible" || style.overflowY !== "visible";
  }

  // The element that scrolls the table sideways: its container, or a box around it (the endpoint list, deployments).
  function scroller(container) {
    for (var element = container; element && element !== document.body; element = element.parentElement) {
      if (element.scrollWidth > element.clientWidth + 1 && clips(element)) {
        return element;
      }
    }
    return null;
  }

  // The part of the window in which the element can be seen: the viewport cut by every box around it that clips.
  function visibleBox(element) {
    var box = { top: 0, left: 0, bottom: window.innerHeight, right: document.documentElement.clientWidth };
    for (var parent = element.parentElement; parent && parent !== document.body; parent = parent.parentElement) {
      if (clips(parent)) {
        var rect = parent.getBoundingClientRect();
        box.top = Math.max(box.top, rect.top);
        box.left = Math.max(box.left, rect.left);
        box.bottom = Math.min(box.bottom, rect.top + parent.clientHeight);
        box.right = Math.min(box.right, rect.right);
      }
    }
    return box;
  }

  function create() {
    bar = document.createElement("div");
    bar.className = "fl-sticky-hscroll";
    bar.setAttribute("aria-hidden", "true");
    inner = document.createElement("div");
    bar.appendChild(inner);
    document.body.appendChild(bar);
    bar.addEventListener("scroll", function () {
      if (target && !syncing) {
        syncing = true;
        target.scrollLeft = bar.scrollLeft;
        syncing = false;
      }
    });
  }

  function update() {
    scheduled = false;
    // With a dialog open only its own tables count: the bar would otherwise lie over the dialog.
    var dialogs = document.querySelectorAll(".mud-dialog");
    var root = dialogs.length ? dialogs[dialogs.length - 1] : document;
    var containers = root.querySelectorAll(".mud-table-container");
    var found = null;
    var foundBox = null;
    for (var i = 0; i < containers.length && !found; i++) {
      var element = scroller(containers[i]);
      if (!element) {
        continue;
      }
      var rect = element.getBoundingClientRect();
      var box = visibleBox(element);
      var bottom = rect.top + element.clientHeight;
      // Its own scrollbar is below the visible part, and enough of the table is in view to be worth scrolling.
      if (rect.width > 0 && bottom > box.bottom + 1 && rect.top < box.bottom - 48 && rect.bottom > box.top) {
        found = element;
        foundBox = { top: box.top, bottom: box.bottom, left: Math.max(rect.left, box.left), right: Math.min(rect.right, box.right) };
      }
    }

    if (!found) {
      target = null;
      if (bar) {
        bar.style.display = "none";
      }
      return;
    }

    if (!bar) {
      create();
    }
    target = found;
    bar.style.display = "block";
    bar.style.left = foundBox.left + "px";
    bar.style.width = Math.max(0, foundBox.right - foundBox.left) + "px";
    inner.style.width = (target.scrollWidth - target.clientWidth + bar.clientWidth) + "px";
    bar.style.top = (foundBox.bottom - bar.offsetHeight) + "px";
    if (Math.abs(bar.scrollLeft - target.scrollLeft) > 1) {
      syncing = true;
      bar.scrollLeft = target.scrollLeft;
      syncing = false;
    }
  }

  function schedule() {
    if (!scheduled) {
      scheduled = true;
      window.requestAnimationFrame(update);
    }
  }

  function start() {
    // Scroll events of every box (capture), the window size, and every render of the circuit.
    document.addEventListener("scroll", function (event) {
      if (event.target !== bar) {
        schedule();
      }
    }, true);
    window.addEventListener("resize", schedule);
    // Changes to the bar itself are left out, or every update would ask for the next one.
    new MutationObserver(function (mutations) {
      for (var i = 0; i < mutations.length; i++) {
        if (mutations[i].target !== bar && mutations[i].target !== inner) {
          schedule();
          return;
        }
      }
    }).observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ["class", "style"] });
    schedule();
  }

  if (document.body) {
    start();
  } else {
    document.addEventListener("DOMContentLoaded", start);
  }
})();
