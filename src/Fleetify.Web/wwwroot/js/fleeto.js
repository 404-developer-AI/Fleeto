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
