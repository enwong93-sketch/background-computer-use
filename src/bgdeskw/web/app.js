"use strict";
(() => {
  const $ = (id) => document.getElementById(id);
  const el = {
    grid: $("grid"), empty: $("empty"), down: $("down"), focus: $("focus"), stage: $("stage"), picture: $("picture"), sink: $("sink"),
    waiting: $("waiting"), title: $("title"), actions: $("actions"), windows: $("windows"), back: $("back"), crumb: $("crumb"),
    conn: $("conn"), connText: $("connText"), status: $("status"), take: $("take"), handback: $("handback"), help: $("help"),
    helpText: $("helpText"), dismiss: $("dismiss"), toast: $("toast"),
  };
  const TILE = { w: 1440, h: 900 };

  let socket = null;
  let layers = [];                 // as last pushed by the host, each with receivedAt
  let agentUp = true;
  let connected = false;
  let receivedLayers = false;
  let retry = 500;
  const cards = new Map();         // layer key -> card parts
  const youHad = new Set();        // layers this page held at the last push, to notice when control ended without us asking
  let panelTimer = 0;
  let nextId = 1;
  const waitingAcks = new Map();

  const focusKey = () => {
    const m = /^#\/layer\/(.+)$/.exec(location.hash);
    if (!m) return null;
    try { return decodeURIComponent(m[1]); } catch { return null; }
  };
  const focusedLayer = () => { const k = focusKey(); return k ? layers.find((l) => l.key === k) : undefined; };
  const nameOf = (l) => l.label || `Agent ${l.slot + 1}`;
  const holding = () => { const l = focusedLayer(); return !!(l && l.you); };

  function ago(ms) {
    const s = Math.round(ms / 1000);
    if (s < 2) return "just now";
    if (s < 60) return `${s}s ago`;
    if (s < 3600) return `${Math.floor(s / 60)}m ago`;
    return `${Math.floor(s / 3600)}h ago`;
  }

  function whatOf(l) {
    if (l.help) return l.help;
    if (l.state === "user") return l.you ? "You are in control" : "Another window has control";
    if (l.doing) return l.doing;
    if (l.last) return `${l.last} · ${ago(l.lastAgoMs + (Date.now() - l.receivedAt))}`;
    return "No actions yet";
  }

  const PILL = { working: "Working", idle: "Idle", needs: "Needs you", user: "You" };

  function makeCard(l) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "card";
    const shot = document.createElement("img");
    shot.className = "shot";
    shot.alt = "";
    shot.draggable = false;
    const info = document.createElement("div");
    info.className = "info";
    const top = document.createElement("div");
    top.className = "row";
    const name = document.createElement("span");
    name.className = "name";
    const pill = document.createElement("span");
    pill.className = "pill";
    const slot = document.createElement("span");
    slot.className = "slot";
    top.append(name, pill, slot);
    const what = document.createElement("div");
    what.className = "what";
    info.append(top, what);
    button.append(shot, info);
    button.addEventListener("click", () => { location.hash = `#/layer/${encodeURIComponent(l.key)}`; });
    return { button, shot, name, pill, slot, what, label: "" };
  }

  function renderCards() {
    const keys = new Set(layers.map((l) => l.key));
    for (const [key, c] of cards) if (!keys.has(key)) { releaseImage(c.shot); c.button.remove(); cards.delete(key); }
    let previous = null;
    for (const l of layers) {
      let c = cards.get(l.key);
      if (!c) { c = makeCard(l); cards.set(l.key, c); }
      const text = nameOf(l);
      if (c.name.textContent !== text) c.name.textContent = text;
      const state = PILL[l.state] || "Idle";
      if (c.pill.textContent !== state) { c.pill.textContent = state; c.pill.className = `pill ${l.state}`; }
      c.button.classList.toggle("needs", l.state === "needs");
      const slot = `Layer ${l.slot + 1}`;
      if (c.slot.textContent !== slot) c.slot.textContent = slot;
      const what = whatOf(l);
      if (c.what.textContent !== what) c.what.textContent = what;
      const label = `${text}, ${state.toLowerCase()}, ${what}`;
      if (c.label !== label) { c.label = label; c.button.setAttribute("aria-label", label); }
      const want = previous ? previous.nextElementSibling : el.grid.firstElementChild;
      if (want !== c.button) el.grid.insertBefore(c.button, want);
      previous = c.button;
    }
  }

  function render() {
    const key = focusKey();
    const focused = focusedLayer();
    if (key && !focused && receivedLayers && agentUp) { location.hash = ""; return; }
    const showFocus = !!focused;
    el.grid.hidden = showFocus;
    el.focus.hidden = !showFocus;
    el.back.hidden = !showFocus;
    el.crumb.hidden = !showFocus;
    el.empty.hidden = showFocus || !agentUp || layers.length > 0 || !receivedLayers;
    el.down.hidden = showFocus || agentUp || !receivedLayers;
    const waiters = layers.filter((l) => l.state === "needs").length;
    if (showFocus) {
      el.title.textContent = nameOf(focused);
      el.crumb.textContent = `Layer ${focused.slot + 1}`;
      document.title = `${waiters ? `(${waiters}) ` : ""}${nameOf(focused)} – Agent workplace`;
      renderControls(focused);
    } else {
      el.title.textContent = "Agent workplace";
      document.title = `${waiters ? `(${waiters}) ` : ""}Agent workplace`;
    }
    if (!showFocus) renderCards();
    setConn();
  }

  function renderControls(l) {
    const mine = !!l.you;
    const other = l.state === "user" && !mine;
    el.take.hidden = mine;
    el.take.disabled = other || !connected;
    el.handback.hidden = !mine;
    el.stage.classList.toggle("controlling", mine);
    el.status.classList.toggle("you", mine);
    el.status.textContent = mine ? "You are in control. The agent can look but not act."
      : other ? "Another window of this page has control."
      : l.state === "working" ? "The agent is working here."
      : l.state === "needs" ? "The agent is waiting for you."
      : "The agent is idle.";
    el.help.hidden = !l.help;
    if (l.help) el.helpText.textContent = l.help;
    if (!mine) { el.stage.classList.remove("typing"); el.picture.style.cursor = ""; }
  }

  function setConn() {
    el.conn.classList.toggle("live", connected && agentUp);
    el.conn.classList.toggle("bad", !connected || !agentUp);
    el.connText.textContent = !connected ? "Reconnecting" : (agentUp ? "Live" : "Desktop not running");
  }

  let toastTimer = 0;
  function toast(text) {
    el.toast.textContent = text;
    el.toast.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { el.toast.hidden = true; }, 6000);
  }

  // ---- the live connection ----
  const urls = new Map();

  function releaseImage(img) {
    const old = urls.get(img);
    if (old) { URL.revokeObjectURL(old); urls.delete(img); }
    img.removeAttribute("src");
  }

  function show(img, blob, done) {
    const url = URL.createObjectURL(blob);
    const old = urls.get(img);
    img.onload = () => { if (old) URL.revokeObjectURL(old); if (done) done(); };
    img.onerror = () => { URL.revokeObjectURL(url); if (urls.get(img) === url) urls.delete(img); };
    urls.set(img, url);
    img.src = url;
  }

  function send(message) {
    if (!socket || socket.readyState !== WebSocket.OPEN) return false;
    socket.send(JSON.stringify(message));
    return true;
  }

  function watch() {
    if (document.hidden) { send({ type: "watch", layers: {} }); return; }
    const key = focusKey();
    const spec = {};
    if (key && layers.some((l) => l.key === key)) {
      const width = Math.max(480, Math.min(1440, Math.round(el.stage.clientWidth * (window.devicePixelRatio || 1)) || 1440));
      spec[key] = { mode: "focus", width, quality: 70 };
    } else {
      for (const l of layers) spec[l.key] = { mode: "thumb" };
    }
    send({ type: "watch", layers: spec });
  }

  function onPicture(data) {
    const view = new DataView(data);
    const length = view.getUint32(0);
    const header = JSON.parse(new TextDecoder().decode(new Uint8Array(data, 4, length)));
    const blob = new Blob([new Uint8Array(data, 4 + length)], { type: "image/jpeg" });
    if (header.mode === "focus") {
      if (header.layer === focusKey()) show(el.picture, blob, () => { el.waiting.hidden = true; });
    } else {
      const c = cards.get(header.layer);
      if (c) show(c.shot, blob);
    }
  }

  function onLayers(m) {
    const known = new Set(layers.map((l) => l.key));
    const now = Date.now();
    layers = (m.layers || []).map((l) => ({ ...l, receivedAt: now }));
    agentUp = m.agent !== false;
    receivedLayers = true;
    // Control that ended without this page asking for it.
    const nowYou = new Set(layers.filter((l) => l.you).map((l) => l.key));
    for (const key of youHad) {
      if (nowYou.has(key)) continue;
      const l = layers.find((x) => x.key === key);
      if (!l) { if (agentUp) toast("The agent's layer is gone."); }
      else if (l.endedReason === "idle") toast("Control went back to the agent: no input for a while.");
      else if (l.endedReason && l.endedReason !== "user") toast("Control went back to the agent.");
    }
    youHad.clear();
    nowYou.forEach((k) => youHad.add(k));
    render();
    if (layers.length !== known.size || layers.some((l) => !known.has(l.key))) watch();
  }

  function connect() {
    socket = new WebSocket(`${location.protocol === "https:" ? "wss" : "ws"}://${location.host}/ws`);
    socket.binaryType = "arraybuffer";
    socket.addEventListener("open", () => { connected = true; retry = 500; setConn(); watch(); });
    socket.addEventListener("message", (e) => {
      if (typeof e.data !== "string") { onPicture(e.data); return; }
      const m = JSON.parse(e.data);
      if (m.type === "layers") onLayers(m);
      else if (m.type === "ack") onAck(m);
    });
    socket.addEventListener("close", () => {
      connected = false;
      press = null;
      pendingText = "";
      clearTimeout(textTimer); textTimer = 0;
      clearTimeout(wheelTimer); wheelTimer = 0; wheelDx = wheelDy = 0;
      layers = layers.map((l) => (l.you ? { ...l, you: false, state: "idle" } : l));
      youHad.clear();
      setConn();
      render();
      setTimeout(connect, retry);
      retry = Math.min(5000, retry * 2);
    });
  }

  function onAck(m) {
    const done = waitingAcks.get(m.id);
    if (done) { waitingAcks.delete(m.id); done(m); }
    if (m.kind === "take" && m.ok && m.taken === false) toast("Another window of this page has control.");
    else if (m.ok === false && m.kind === "input" && /not in control/.test(m.error || "")) { youHad.delete(m.layer); toast("You are no longer in control."); }
  }

  // ---- taking over ----
  function ask(message) {
    const id = nextId++;
    message.id = id;
    send(message);
    return new Promise((resolve) => { waitingAcks.set(id, resolve); setTimeout(() => { if (waitingAcks.delete(id)) resolve({ ok: false, error: "no answer" }); }, 4000); });
  }

  el.take.addEventListener("click", async () => {
    const key = focusKey();
    if (!key) return;
    await ask({ type: "take", layer: key });
    el.sink.focus({ preventScroll: true });
  });
  el.handback.addEventListener("click", async () => {
    const key = focusKey();
    if (!key) return;
    releaseDrag();
    flushText();
    await ask({ type: "release", layer: key });
    el.take.focus({ preventScroll: true });
  });
  el.dismiss.addEventListener("click", () => { const key = focusKey(); if (key) ask({ type: "dismiss", layer: key }); });

  // The picture's place on the page, and a point on it as a point of the agent's screen.
  function toTile(event, clamp) {
    const box = el.picture.getBoundingClientRect();
    const scale = Math.min(box.width / TILE.w, box.height / TILE.h);
    if (!(scale > 0)) return null;
    const w = TILE.w * scale, h = TILE.h * scale;
    const x = (event.clientX - box.left - (box.width - w) / 2) / scale;
    const y = (event.clientY - box.top - (box.height - h) / 2) / scale;
    if (clamp) return { x: Math.max(0, Math.min(TILE.w - 1, Math.floor(x))), y: Math.max(0, Math.min(TILE.h - 1, Math.floor(y))) };
    if (x < 0 || y < 0 || x >= TILE.w || y >= TILE.h) return null;
    return { x: Math.floor(x), y: Math.floor(y) };
  }

  function input(message) {
    const key = focusKey();
    if (!key || !holding()) return;
    send({ type: "input", layer: key, id: nextId++, ...message });
  }

  // Mouse, pen and touch: a press that stays put is a click; one that moves becomes a drag, started where it began.
  let press = null;
  let lastClick = null;
  let lastMove = 0;
  let wheelDx = 0, wheelDy = 0, wheelAt = null, wheelTimer = 0;
  const BUTTONS = { 0: "left", 1: "middle", 2: "right" };

  function releaseDrag() {
    if (!press) return;
    const p = press;
    press = null;
    if (p.dragging) input({ kind: "up", x: p.last.x, y: p.last.y });
  }

  el.stage.addEventListener("contextmenu", (e) => { if (holding()) e.preventDefault(); });

  el.stage.addEventListener("pointerdown", (e) => {
    if (!holding() || press || e.isPrimary === false) return;
    flushText();
    const at = toTile(e, false);
    el.sink.focus({ preventScroll: true });
    if (!at) return;
    e.preventDefault();
    try { el.stage.setPointerCapture(e.pointerId); } catch { /* synthetic events have no pointer to capture */ }
    press = {
      id: e.pointerId, start: at, clientX: e.clientX, clientY: e.clientY,
      threshold: e.pointerType === "touch" ? 12 : 6,
      button: BUTTONS[e.button] || "left", dragging: false, last: at, mods: modifiers(e),
    };
  });

  el.stage.addEventListener("pointermove", (e) => {
    if (!holding()) return;
    if (press && press.id === e.pointerId) {
      const at = toTile(e, true) || press.last;
      if (!press.dragging && Math.hypot(e.clientX - press.clientX, e.clientY - press.clientY) > press.threshold) {
        press.dragging = true;
        input({ kind: "down", x: press.start.x, y: press.start.y, button: press.button });
      }
      if (press.dragging) {
        const now = performance.now();
        if (now - lastMove >= 25) { lastMove = now; input({ kind: "move", x: at.x, y: at.y }); }
        press.last = at;
      }
    } else if (e.pointerType === "mouse") {
      const at = toTile(e, false);
      if (!at) return;
      const now = performance.now();
      if (now - lastMove >= 50) { lastMove = now; input({ kind: "move", x: at.x, y: at.y }); }
    }
  });

  function finishPress(e, cancelled) {
    if (!press || press.id !== e.pointerId) return;
    const p = press;
    press = null;
    const at = toTile(e, true) || p.last;
    if (p.dragging) input({ kind: "up", x: at.x, y: at.y });
    else if (!cancelled) {
      // A second quick press in the same place is the second click of a double click (pointerup itself has detail === 0).
      const now = performance.now();
      const isDouble = p.button === "left" && lastClick && now - lastClick.at <= 400
        && Math.hypot(p.start.x - lastClick.x, p.start.y - lastClick.y) <= 6;
      lastClick = isDouble ? null : { at: now, x: p.start.x, y: p.start.y };
      input({ kind: "click", x: p.start.x, y: p.start.y, button: p.button, count: 1, modifiers: p.mods || undefined, double: isDouble || undefined });
    }
  }
  el.stage.addEventListener("pointerup", (e) => finishPress(e, false));
  el.stage.addEventListener("pointercancel", (e) => finishPress(e, true));
  el.stage.addEventListener("lostpointercapture", () => releaseDrag());

  el.stage.addEventListener("wheel", (e) => {
    if (!holding()) return;
    e.preventDefault();
    const at = toTile(e);
    if (!at) return;
    const unit = e.deltaMode === 1 ? 40 : e.deltaMode === 2 ? 400 : 1;
    wheelDx += e.deltaX * unit; wheelDy += e.deltaY * unit; wheelAt = at;
    if (!wheelTimer) wheelTimer = setTimeout(() => {
      wheelTimer = 0;
      if (wheelAt) input({ kind: "wheel", x: wheelAt.x, y: wheelAt.y, dx: Math.round(wheelDx), dy: Math.round(wheelDy) });
      wheelDx = wheelDy = 0;
    }, 40);
  }, { passive: false });

  function modifiers(e) {
    const m = [];
    if (e.ctrlKey) m.push("ctrl");
    if (e.shiftKey) m.push("shift");
    if (e.altKey) m.push("alt");
    return m.join("+");
  }

  // ---- the keyboard ----
  // Letters and other text arrive through the hidden text box (so input methods work); keys that are not text, and
  // anything held with Ctrl or Alt, are sent as key presses.
  const NAMED = {
    Enter: "enter", Backspace: "backspace", Tab: "tab", Escape: "esc", Delete: "delete", Insert: "insert", Home: "home", End: "end",
    PageUp: "pageup", PageDown: "pagedown", ArrowLeft: "left", ArrowRight: "right", ArrowUp: "up", ArrowDown: "down", " ": "space",
  };
  let pendingText = "";
  let textTimer = 0;

  function queueText(text) {
    if (!text) return;
    pendingText += text;
    if (!textTimer) textTimer = setTimeout(flushText, 15);
  }
  function flushText() {
    clearTimeout(textTimer);
    textTimer = 0;
    while (pendingText) {
      let end = Math.min(pendingText.length, 1500);
      // Do not split a surrogate pair across two messages.
      if (end < pendingText.length && pendingText.charCodeAt(end - 1) >= 0xd800 && pendingText.charCodeAt(end - 1) <= 0xdbff) end--;
      const part = pendingText.slice(0, end);
      pendingText = pendingText.slice(part.length);
      input({ kind: "text", text: part });
    }
  }

  el.sink.addEventListener("focus", () => el.stage.classList.toggle("typing", holding()));
  el.sink.addEventListener("blur", () => el.stage.classList.remove("typing"));

  el.sink.addEventListener("keydown", (e) => {
    if (!holding()) return;
    if (e.isComposing || e.keyCode === 229 || e.key === "Process" || e.key === "Dead") return;
    // Ctrl+Alt+Escape hands control back from the keyboard alone (since plain Escape and Tab go to the agent's window).
    if (e.ctrlKey && e.altKey && e.key === "Escape") { e.preventDefault(); el.handback.click(); return; }
    if (["Shift", "Control", "Alt", "AltGraph", "Meta", "CapsLock", "ContextMenu"].includes(e.key)) { e.preventDefault(); return; }
    if (e.getModifierState && e.getModifierState("AltGraph")) return;      // arrives as a character
    const mods = [];
    if (e.ctrlKey) mods.push("ctrl");
    if (e.altKey) mods.push("alt");
    if (e.shiftKey) mods.push("shift");
    const plainText = e.key.length === 1 && !e.ctrlKey && !e.altKey && !e.metaKey;
    if (plainText) return;                                           // arrives as text
    // Pasting goes in as the text itself (see the paste handler); the same for cutting and copying in the page's own box.
    if (e.ctrlKey && !e.altKey && !e.shiftKey && e.key.toLowerCase() === "v") return;
    if (e.metaKey) return;
    let main = NAMED[e.key];
    if (!main && /^F([1-9]|1[0-2])$/.test(e.key)) main = e.key.toLowerCase();
    if (!main && e.key.length === 1) main = e.key.toLowerCase();
    if (!main) return;
    e.preventDefault();
    flushText();
    input({ kind: "key", keys: [...mods, main].join("+") });
  });

  el.sink.addEventListener("paste", (e) => {
    if (!holding()) return;
    e.preventDefault();
    queueText(e.clipboardData?.getData("text/plain") || "");
  });

  el.sink.addEventListener("beforeinput", (e) => {
    if (!holding()) { e.preventDefault(); return; }
    if (e.isComposing || e.inputType === "insertCompositionText") return;
    if (e.inputType === "insertText" && e.data) { e.preventDefault(); queueText(e.data); }
    else if (e.inputType === "insertLineBreak" || e.inputType === "insertParagraph") e.preventDefault();
  });
  el.sink.addEventListener("compositionend", (e) => { if (holding()) queueText(e.data); el.sink.value = ""; });
  el.sink.addEventListener("input", (e) => { if (!e.isComposing) el.sink.value = ""; });

  // ---- the side panel of one agent ----
  const ROUTE = { "in-place": "Direct", "real": "Real input", "in-place, then real": "Direct, then real input", "held": "Waited for you" };

  async function fetchJson(path) {
    const r = await fetch(path, { cache: "no-store" });
    if (!r.ok) throw new Error(String(r.status));
    return r.json();
  }

  const rows = new Map();          // activity id -> list item

  function renderAction(e, key) {
    const li = document.createElement("li");
    if (e.note) { li.className = "note"; li.textContent = e.summary; li.dataset.id = e.id; return li; }
    const mine = e.user;
    li.className = [e.ok ? "" : "failed", mine ? "mine" : ""].join(" ").trim();
    li.dataset.id = e.id;
    const time = document.createElement("time");
    time.dateTime = new Date(e.at).toISOString();
    time.textContent = new Date(e.at).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false });
    const text = document.createElement("span");
    text.className = "text";
    text.textContent = mine ? e.summary.replace(/^You: /, "") : e.summary;
    li.append(time, text);
    if (ROUTE[e.route]) { const how = document.createElement("span"); how.className = "how"; how.textContent = ROUTE[e.route]; li.append(how); }
    if (e.thumb) li.append(afterPicture(key, e.id));
    return li;
  }

  function afterPicture(key, id) {
    const img = document.createElement("img");
    img.className = "after";
    img.alt = "The screen just after this action";
    img.loading = "lazy";
    img.draggable = false;
    img.src = `/thumb/${encodeURIComponent(key)}/${id}.jpg`;
    img.addEventListener("error", () => img.remove());
    return img;
  }

  async function refreshPanels() {
    const key = focusKey();
    if (!key) return;
    const base = `/api/layer/${encodeURIComponent(key)}`;
    try {
      const [a, w] = await Promise.all([fetchJson(`${base}/activity?n=40`), fetchJson(`${base}/windows`)]);
      if (key !== focusKey()) return;
      const list = (a.activity || []).slice().reverse().slice(0, 30);
      const ids = new Set(list.map((e) => e.id));
      for (const [id, li] of rows) if (!ids.has(id)) { li.remove(); rows.delete(id); }
      let previous = null;
      for (const e of list) {
        let li = rows.get(e.id);
        // An entry gets its picture a moment after it appears.
        if (li && e.thumb && !li.querySelector(".after") && !li.classList.contains("note")) li.append(afterPicture(key, e.id));
        if (!li) { li = renderAction(e, key); rows.set(e.id, li); }
        const want = previous ? previous.nextElementSibling : el.actions.firstElementChild;
        if (want !== li) el.actions.insertBefore(li, want);
        previous = li;
      }
      el.actions.querySelectorAll(".none-yet").forEach((n) => n.remove());
      if (!list.length) el.actions.replaceChildren(none("Nothing yet"));
      if (w.windows) {
        el.windows.replaceChildren(...w.windows.map((x) => {
          const li = document.createElement("li");
          li.textContent = x.title;
          if (x.process) { const p = document.createElement("div"); p.className = "proc"; p.textContent = x.process; li.append(p); }
          return li;
        }));
        if (!w.windows.length) el.windows.replaceChildren(none("No windows"));
      }
    } catch { /* the next round tries again */ }
  }

  function none(text) {
    const li = document.createElement("li");
    li.className = "none-yet";
    li.textContent = text;
    return li;
  }

  function enter() {
    releaseDrag();
    clearInterval(panelTimer);
    rows.clear();
    el.actions.replaceChildren();
    el.windows.replaceChildren();
    releaseImage(el.picture);
    el.waiting.hidden = false;
    render();
    watch();
    if (focusKey() && !document.hidden) { refreshPanels(); panelTimer = setInterval(refreshPanels, 1200); }
  }

  // ---- wiring ----
  window.addEventListener("hashchange", enter);
  window.addEventListener("blur", () => releaseDrag());
  document.addEventListener("visibilitychange", () => {
    releaseDrag();
    watch();
    clearInterval(panelTimer);
    if (focusKey() && !document.hidden) { refreshPanels(); panelTimer = setInterval(refreshPanels, 1200); }
  });
  el.back.addEventListener("click", () => { location.hash = ""; });
  document.addEventListener("keydown", (e) => { if (e.key === "Escape" && focusKey() && !holding()) location.hash = ""; });
  let resizeTimer = 0;
  window.addEventListener("resize", () => { clearTimeout(resizeTimer); resizeTimer = setTimeout(watch, 250); });
  setInterval(() => { if (receivedLayers && !focusKey() && !document.hidden) renderCards(); }, 1000);
  // The page was closed or left while holding a layer: the host notices the connection ending and hands it back.
  window.addEventListener("pagehide", () => { try { socket && socket.close(); } catch { /* nothing to do */ } });

  // For tests: what the page believes, without scraping it.
  window.__workplace = { state: () => ({ layers, connected, holding: holding() }), toTile };

  setConn();
  connect();
  enter();
})();
