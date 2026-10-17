// Unbounded Panorama 360, container edition: the web page.
// Plain modules, no build step: the server streams frames to the cooler screen; this page edits the design.

const $ = (selector, root = document) => root.querySelector(selector);

// Optional parts are written as `cond ? el : null`: skip them (the DOM would show the word "null").
const nativeReplaceChildren = Element.prototype.replaceChildren;
Element.prototype.replaceChildren = function (...children) {
  return nativeReplaceChildren.apply(this, children.flat(Infinity).filter(c => c !== null && c !== undefined && c !== false));
};

/** Small element builder: h("div", { class: "x", onclick }, child, "text"). */
function h(tag, attrs = {}, ...children) {
  const el = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs ?? {})) {
    if (value === undefined || value === null || value === false) continue;
    if (key.startsWith("on") && typeof value === "function") el.addEventListener(key.slice(2), value);
    else if (key === "class") el.className = value;
    else if (key === "style" && typeof value === "object") Object.assign(el.style, value);
    else if (key in el && typeof value !== "string") el[key] = value;
    else el.setAttribute(key, value === true ? "" : value);
  }
  for (const child of children.flat(Infinity)) {
    if (child === null || child === undefined || child === false) continue;
    el.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return el;
}

async function api(path, { method = "GET", body, raw } = {}) {
  const init = { method, headers: {} };
  if (body !== undefined) {
    if (raw) init.body = body;
    else { init.body = JSON.stringify(body); init.headers["Content-Type"] = "application/json"; }
  }
  const response = await fetch("/api" + path, init);
  if (response.status === 401) { showSignIn(); throw new Error("Sign in first."); }
  const text = await response.text();
  const data = text ? (() => { try { return JSON.parse(text); } catch { return text; } })() : null;
  if (!response.ok) throw new Error(data?.error || (typeof data === "string" && data) || response.statusText);
  return data;
}

let toastTimer;
function toast(message, ms = 3500) {
  const el = $("#toast");
  el.textContent = message;
  el.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => (el.hidden = true), ms);
}

function debounce(fn, ms) {
  let timer;
  const wrapped = (...args) => { clearTimeout(timer); timer = setTimeout(() => fn(...args), ms); };
  wrapped.now = (...args) => { clearTimeout(timer); return fn(...args); };
  return wrapped;
}

/** A random id; crypto.randomUUID only exists on HTTPS pages, and this page is usually plain HTTP on the LAN. */
function uuid() {
  const b = crypto.getRandomValues(new Uint8Array(16));
  b[6] = (b[6] & 0x0f) | 0x40; b[8] = (b[8] & 0x3f) | 0x80;
  const x = [...b].map(v => v.toString(16).padStart(2, "0")).join("");
  return `${x.slice(0, 8)}-${x.slice(8, 12)}-${x.slice(12, 16)}-${x.slice(16, 20)}-${x.slice(20)}`;
}

/** Sends the latest value as changes happen, one request at a time (the preview follows a drag). */
function saver(send) {
  let busy = false, pending = false;
  const run = async () => {
    if (busy) { pending = true; return; }
    busy = true;
    try { await send(); } finally { busy = false; if (pending) { pending = false; run(); } }
  };
  return run;
}

const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));
const fmtTime = s => { s = Math.max(0, Math.round(s)); const m = Math.floor(s / 60); return `${m}:${String(s % 60).padStart(2, "0")}`; };
function parseTime(text) {
  const parts = String(text).trim().split(":").map(Number);
  if (parts.some(Number.isNaN)) return NaN;
  return parts.reduce((total, p) => total * 60 + p, 0);
}
const fmtBytes = b => b > 1e9 ? (b / 1e9).toFixed(1) + " GB" : (b / 1e6).toFixed(1) + " MB";

/** Colour field: native picker (any colour, black included) + hex text + optional swatches. */
function colorInput(value, onChange, swatches = []) {
  const picker = h("input", { type: "color", value: normalizeHex(value) });
  const text = h("input", { type: "text", value: normalizeHex(value), maxLength: 7 });
  const set = v => { const hex = normalizeHex(v); picker.value = hex; text.value = hex; onChange(hex); };
  picker.addEventListener("input", () => set(picker.value));
  text.addEventListener("change", () => /^#?[0-9a-f]{6}$/i.test(text.value) ? set(text.value) : (text.value = picker.value));
  return h("div", { class: "stack" },
    h("div", { class: "color" }, picker, text),
    swatches.length ? h("div", { class: "swatches" }, swatches.map(c => h("button", { class: "swatch", type: "button", title: c, style: { background: c }, onclick: () => set(c) }))) : null);
}
const normalizeHex = v => { const s = String(v ?? "").trim().replace(/^#/, ""); return /^[0-9a-f]{6}$/i.test(s) ? "#" + s.toUpperCase() : "#FFFFFF"; };
const SWATCHES = ["#FFFFFF", "#000000", "#00E5FF", "#8B5CF6", "#FF4D6D", "#FF7A59", "#FFD166", "#3DDC97", "#3A86FF", "#BFC7D5"];

function field(label, control) { return h("label", { class: "field" }, h("span", {}, label), control); }
function select(options, value, onChange) {
  const el = h("select", { onchange: () => onChange(el.value) },
    options.map(o => typeof o === "string" ? h("option", { value: o, selected: o === value }, o) : h("option", { value: o.value, selected: o.value === value }, o.label)));
  return el;
}
function check(label, value, onChange) {
  const box = h("input", { type: "checkbox", checked: !!value, onchange: () => onChange(box.checked) });
  return h("label", { class: "check" }, box, label);
}
function range(min, max, value, onChange, step = 1) {
  const out = h("span", { class: "muted small" }, value);
  const input = h("input", { type: "range", min, max, step, value, oninput: () => { out.textContent = input.value; onChange(Number(input.value)); } });
  return h("div", { class: "row" }, h("div", { class: "grow" }, input), out);
}
function number(value, onChange, attrs = {}) {
  const el = h("input", { type: "number", value, ...attrs, onchange: () => el.value !== "" && onChange(Number(el.value)) });
  return el;
}

// ---------------------------------------------------------------------------------------------- shell

let status = null;
let current = null; // the page shown: { dispose() }

async function refreshStatus() {
  try {
    status = await api("/status");
    const s = status.screen;
    $("#navStatus").replaceChildren(
      h("div", {}, h("span", { class: "dot " + (s.connected ? "on" : "warn") }), s.connected ? `${s.name}` : "Screen not connected"),
      s.connected ? h("div", {}, `${s.fps} fps`) : (s.error ? h("div", { class: "small" }, s.error) : null),
      h("div", {}, h("span", { class: "dot " + (status.lighting.connected ? "on" : "warn") }), status.lighting.connected ? "OpenRGB" : "OpenRGB offline"),
      status.proxmox ? h("div", {}, h("span", { class: "dot " + (status.proxmox.working ? "on" : "warn") }), "Proxmox stats") : null,
      h("div", { class: "small", style: { color: "var(--dim)" } }, `v${status.version}`));
  } catch { /* shown on next poll */ }
}

function showSignIn() {
  const box = $("#signin");
  if (!box.hidden) return;
  box.hidden = false;
  const form = $("form", box);
  form.onsubmit = async e => {
    e.preventDefault();
    try {
      await fetch("/api/auth", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ password: form.password.value }) })
        .then(r => { if (!r.ok) throw new Error("Wrong password."); });
      box.hidden = true;
      route();
    } catch (err) {
      const p = $(".error", form); p.textContent = err.message; p.hidden = false;
    }
  };
  form.password.focus();
}

const pages = { studio: Studio, library: Library, argb: Argb, settings: Settings };

function route() {
  const name = (location.hash.replace(/^#\/?/, "") || "studio").split("/")[0];
  const page = pages[name] ? name : "studio";
  document.querySelectorAll(".nav").forEach(a => a.classList.toggle("active", a.dataset.page === page));
  current?.dispose?.();
  const root = $("#page");
  root.replaceChildren();
  current = pages[page](root) ?? null;
}

window.addEventListener("hashchange", route);
refreshStatus();
setInterval(refreshStatus, 2000);
api("/auth").then(a => (a.required && !a.signedIn ? showSignIn() : route())).catch(() => route());

// ---------------------------------------------------------------------------------------------- studio

const CONTENT = [
  { value: "Clock", label: "Clock" }, { value: "Date", label: "Date" }, { value: "Sensor", label: "Live value (sensor)" },
  { value: "Text", label: "Text" }, { value: "CpuName", label: "CPU name" },
];
const CLOCK_FORMATS = ["HH:mm", "HH:mm:ss", "h:mm tt", "h:mm"];
const DATE_FORMATS = ["dddd d MMMM", "ddd d MMM", "dd/MM/yyyy", "MM/dd/yyyy", "yyyy-MM-dd", "dddd"];
const BADGES = [["#A92F2C", "#CB6236"], ["#0068B5", "#566D98"], ["#00A3B4", "#8B5CF6"], ["#1E7F4F", "#3DDC97"], ["#3A1C71", "#D76D77"], ["#232526", "#414345"]];

function Studio(root) {
  let design = null, sensors = [], texts = {}, library = [];
  let selected = null; // widget id, or "bg"
  let tab = "widgets";
  let disposed = false;
  let drawing = null; // { canvas, ctx, color, size, eraser, dirty }
  const screen = () => ({ w: status?.screen?.width ?? 320, h: status?.screen?.height ?? 320 });

  const live = h("img", { class: "live", alt: "", draggable: false });
  const layer = h("div", { class: "layer" });
  const stage = h("div", { class: "stage" }, live, layer);
  const chips = h("div", { class: "row" });
  const panel = h("div");
  root.append(
    h("div", { class: "head" }, h("div", {}, h("h1", {}, "Studio"), h("div", { class: "muted" }, "Exactly what your cooler shows, live. Drag text to move it; drop a picture or video on the screen to use it as background."))),
    h("div", { class: "studio" },
      h("div", { class: "stage-wrap" }, stage, h("div", { class: "stage-bar" }, chips)),
      h("div", {}, panel)));

  // Live frame: the next one is requested when the last has arrived (about 8 per second).
  let frameTimer;
  const nextFrame = () => { if (!disposed) live.src = "/api/preview.jpg?t=" + Date.now(); };
  live.onload = live.onerror = () => { frameTimer = setTimeout(nextFrame, 110); };
  nextFrame();

  const save = saver(async () => {
    try {
      const saved = await api("/design", { method: "PUT", body: design });
      design.revision = saved.revision;
    } catch (err) { toast("Not saved: " + err.message); }
  });
  const changed = () => { drawLayer(); save(); };

  async function load() {
    [design, library] = await Promise.all([api("/design"), api("/library")]);
    refreshSensors();
    render();
  }
  async function refreshSensors() {
    try {
      [sensors, texts] = await Promise.all([api("/sensors"), api("/text")]);
      drawLayer();
      // Live text in the list, updated in place (a full redraw would interrupt typing in the editor).
      panel.querySelectorAll(".widget-item .name[data-id]").forEach(el => {
        const w = design?.widgets.find(x => x.id === el.dataset.id);
        if (w) el.textContent = describe(w);
      });
    } catch { }
  }
  const sensorTimer = setInterval(refreshSensors, 1500);

  // ------------------------------------------------------------ the stage overlay

  const scale = () => stage.clientWidth / screen().w;
  const widgetBox = w => {
    // Measured like the renderer: Roboto at the widget's size, aligned in its box, pill padding for badges.
    const ctx = measure.getContext("2d");
    ctx.font = `${{ Light: 300, Regular: 400, Medium: 500, Bold: 700 }[w.weight] ?? 400} ${w.size}px Roboto`;
    const text = texts[w.id] ?? w.text ?? "";
    const pad = w.badge ? w.size * 0.7 : 0;
    const textW = Math.min(w.width, ctx.measureText(text).width + pad);
    const left = w.align === "Center" ? w.x + (w.width - textW) / 2 : w.align === "Right" ? w.x + w.width - textW : w.x;
    return { x: w.x, y: w.y, width: w.width, height: w.size * 1.3, textLeft: left, textWidth: textW };
  };
  const measure = h("canvas", { width: 10, height: 10 });

  function drawLayer() {
    if (!design || drawing) return;
    const s = scale();
    const children = [];
    if (selected === "bg" || tab === "background") {
      const p = bgPlacement();
      if (p) children.push(h("div", { class: "bg-frame", style: { left: p.x * s + "px", top: p.y * s + "px", width: p.width * s + "px", height: p.height * s + "px" } }));
    }
    for (const w of design.widgets) {
      if (w.hidden) continue;
      const b = widgetBox(w);
      const box = h("div", {
        class: "box" + (selected === w.id ? " sel" : "") + (w.locked ? " locked" : ""),
        style: { left: b.x * s + "px", top: b.y * s + "px", width: b.width * s + "px", height: b.height * s + "px" },
        onpointerdown: e => startDrag(e, w, "move"),
      }, h("div", { class: "grip", onpointerdown: e => { e.stopPropagation(); startDrag(e, w, "resize"); } }));
      children.push(box);
    }
    layer.replaceChildren(...children);
    chips.replaceChildren(
      h("span", { class: "chip" }, `${screen().w}×${screen().h}`),
      status?.screen?.media ? h("span", { class: "chip" }, status.screen.media) : null,
      selected && selected !== "bg" ? h("span", { class: "chip" }, "Arrows move · Shift for 10 px · Del removes") : null);
  }

  function startDrag(e, w, mode) {
    e.preventDefault();
    if (selected !== w.id) { selected = w.id; tab = "widgets"; renderPanel(); }
    drawLayer();
    if (w.locked) return;
    stage.setPointerCapture(e.pointerId);
    const s = scale(), start = { x: e.clientX, y: e.clientY, wx: w.x, wy: w.y, ww: w.width };
    const move = ev => {
      const dx = (ev.clientX - start.x) / s, dy = (ev.clientY - start.y) / s;
      if (mode === "move") {
        let x = Math.round(start.wx + dx), y = Math.round(start.wy + dy);
        const { w: sw } = screen();
        if (Math.abs(x + w.width / 2 - sw / 2) < 4) x = Math.round(sw / 2 - w.width / 2); // snap to the centre line
        w.x = x; w.y = y;
      } else {
        w.width = Math.max(20, Math.round(start.ww + dx));
      }
      changed();
    };
    const up = () => { stage.removeEventListener("pointermove", move); stage.removeEventListener("pointerup", up); renderPanel(); };
    stage.addEventListener("pointermove", move);
    stage.addEventListener("pointerup", up);
  }

  // Background: drag on an empty spot moves it, the wheel zooms (around the pointer).
  const bgItem = () => library.find(i => i.id === design?.background?.mediaId);
  function bgPlacement() {
    const item = bgItem(); if (!item) return null;
    const { w: sw, h: sh } = screen(), b = design.background;
    if (b.width) { const height = b.width * item.height / item.width; return { x: b.x ?? (sw - b.width) / 2, y: b.y ?? (sh - height) / 2, width: b.width, height }; }
    const k = Math.max(sw / item.width, sh / item.height);
    return { x: (sw - item.width * k) / 2, y: (sh - item.height * k) / 2, width: item.width * k, height: item.height * k };
  }
  stage.addEventListener("pointerdown", e => {
    if (drawing || e.target !== layer && !e.target.classList.contains("bg-frame")) return;
    selected = bgItem() ? "bg" : null;
    if (selected) tab = "background";
    renderPanel(); drawLayer();
    const p = bgPlacement(); if (!p) return;
    stage.setPointerCapture(e.pointerId);
    const s = scale(), start = { x: e.clientX, y: e.clientY, px: p.x, py: p.y };
    const move = ev => {
      Object.assign(design.background, { x: Math.round(start.px + (ev.clientX - start.x) / s), y: Math.round(start.py + (ev.clientY - start.y) / s), width: Math.round(p.width) });
      changed();
    };
    const up = () => { stage.removeEventListener("pointermove", move); stage.removeEventListener("pointerup", up); };
    stage.addEventListener("pointermove", move);
    stage.addEventListener("pointerup", up);
  });
  stage.addEventListener("wheel", e => {
    const p = bgPlacement(); if (!p || drawing || selected !== "bg") return;
    e.preventDefault();
    const s = scale(), rect = stage.getBoundingClientRect();
    const cx = (e.clientX - rect.left) / s, cy = (e.clientY - rect.top) / s;
    const k = e.deltaY < 0 ? 1.08 : 1 / 1.08;
    const width = clamp(p.width * k, 40, 8000);
    const f = width / p.width;
    Object.assign(design.background, { width: Math.round(width), x: Math.round(cx - (cx - p.x) * f), y: Math.round(cy - (cy - p.y) * f) });
    changed();
  }, { passive: false });

  // Drop a file on the screen: upload, then use it as the background.
  stage.addEventListener("dragover", e => { e.preventDefault(); stage.classList.add("drop"); });
  stage.addEventListener("dragleave", () => stage.classList.remove("drop"));
  stage.addEventListener("drop", async e => {
    e.preventDefault(); stage.classList.remove("drop");
    const file = e.dataTransfer.files[0]; if (!file) return;
    const id = await uploadFiles([file], text => chips.replaceChildren(h("span", { class: "chip" }, text)));
    if (id) { library = await api("/library"); useBackground(id); }
  });

  const onKey = e => {
    if (!design || drawing || ["INPUT", "SELECT", "TEXTAREA"].includes(document.activeElement?.tagName)) return;
    const w = design.widgets.find(x => x.id === selected); if (!w) return;
    const step = e.shiftKey ? 10 : 1;
    const moves = { ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, -step], ArrowDown: [0, step] };
    if (moves[e.key] && !w.locked) { e.preventDefault(); w.x += moves[e.key][0]; w.y += moves[e.key][1]; changed(); renderPanel(); }
    else if (e.key === "Delete") { removeWidget(w); }
  };
  document.addEventListener("keydown", onKey);
  const onResize = () => drawLayer();
  window.addEventListener("resize", onResize);

  // ------------------------------------------------------------ side panel

  function render() { renderPanel(); drawLayer(); }

  function renderPanel() {
    if (!design) return;
    const tabs = h("div", { class: "tabs" },
      [["widgets", "Text & values"], ["background", "Background"], ["draw", "Draw"]].map(([key, label]) =>
        h("button", { class: tab === key ? "on" : "", onclick: async () => {
          if (tab === "draw" && key !== "draw") stopDrawing();
          tab = key;
          if (key === "draw") startDrawing();
          // Imports finished on the Library page since this page opened must be offered too.
          if (key === "background") library = await api("/library");
          render();
        } }, label)));
    panel.replaceChildren(tabs, tab === "widgets" ? widgetsPanel() : tab === "background" ? backgroundPanel() : drawPanel());
  }

  function widgetsPanel() {
    const { w: sw } = screen();
    const add = content => {
      const size = content === "Clock" ? 96 : content === "Sensor" ? 44 : 28;
      const w = { id: uuid(), content, text: "Text", sensorId: null, decimals: 0, showUnit: true, prefix: content === "Sensor" ? "CPU " : "", suffix: "",
        format: content === "Date" ? "dddd d MMMM" : "HH:mm", x: 0, y: Math.round(screen().h / 2 - size * 0.65), width: sw, weight: content === "Clock" ? "Light" : "Regular",
        size, color: "#FFFFFF", align: "Center", badge: false, badgeFrom: "#A92F2C", badgeTo: "#CB6236", hidden: false, locked: false };
      if (content === "Sensor") w.sensorId = sensors.find(s => s.category === "CPU" && s.type === "Temperature")?.id ?? sensors[0]?.id ?? null;
      design.widgets.push(w); selected = w.id; changed(); render();
    };
    const list = h("div", { class: "widget-list" }, design.widgets.map((w, i) =>
      h("div", { class: "widget-item" + (selected === w.id ? " sel" : ""), onclick: () => { selected = w.id; render(); } },
        h("span", { class: "name", "data-id": w.id }, describe(w)),
        h("button", { class: "icon", title: w.hidden ? "Show" : "Hide", onclick: e => { e.stopPropagation(); w.hidden = !w.hidden; changed(); render(); } }, w.hidden ? "◌" : "●"),
        h("button", { class: "icon", title: "Move up", disabled: i === 0, onclick: e => { e.stopPropagation(); design.widgets.splice(i - 1, 0, design.widgets.splice(i, 1)[0]); changed(); render(); } }, "↑"),
        h("button", { class: "icon", title: "Move down", disabled: i === design.widgets.length - 1, onclick: e => { e.stopPropagation(); design.widgets.splice(i + 1, 0, design.widgets.splice(i, 1)[0]); changed(); render(); } }, "↓"))));
    const w = design.widgets.find(x => x.id === selected);
    return h("div", {},
      h("div", { class: "card stack" },
        h("div", { class: "row" }, h("h2", { class: "grow" }, "On the screen"),
          ...["Clock", "Date", "Sensor", "Text"].map(c => h("button", { onclick: () => add(c) }, "+ " + (c === "Sensor" ? "Value" : c)))),
        design.widgets.length ? list : h("p", { class: "muted" }, "Nothing yet: add a clock, the date, a live value or your own text.")),
      w ? widgetEditor(w) : null);
  }

  function describe(w) {
    const t = texts[w.id];
    const kind = { Clock: "Clock", Date: "Date", Sensor: "Value", Text: "Text", CpuName: "CPU name", GpuName: "GPU name" }[w.content];
    return `${kind}: ${t ?? w.text}`;
  }

  function widgetEditor(w) {
    const set = (key, value, rerender = false) => { w[key] = value; changed(); if (rerender) renderPanel(); };
    const groups = Object.groupBy ? Object.groupBy(sensors, s => `${s.category} · ${s.hardware}`) : sensors.reduce((g, s) => ((g[`${s.category} · ${s.hardware}`] ??= []).push(s), g), {});
    const sensorSelect = h("select", { onchange: e => set("sensorId", e.target.value) },
      h("option", { value: "" }, sensors.length ? "Choose a value…" : "No sensors yet"),
      Object.entries(groups).map(([group, items]) => h("optgroup", { label: group },
        items.map(s => h("option", { value: s.id, selected: s.id === w.sensorId }, `${s.name}  (${s.value})`)))));
    return h("div", { class: "card stack" },
      h("div", { class: "row" }, h("h2", { class: "grow" }, "Selected"),
        h("button", { onclick: () => { const copy = { ...structuredClone(w), id: uuid(), y: w.y + Math.round(w.size * 1.3) }; design.widgets.push(copy); selected = copy.id; changed(); render(); } }, "Duplicate"),
        h("button", { class: "danger", onclick: () => removeWidget(w) }, "Remove")),
      field("Shows", select(CONTENT, w.content, v => set("content", v, true))),
      w.content === "Text" ? field("Text", h("input", { type: "text", value: w.text, oninput: e => set("text", e.target.value) })) : null,
      w.content === "Clock" || w.content === "Date" ? field("Format", h("div", { class: "row" },
        h("input", { type: "text", class: "grow", value: w.format, oninput: e => set("format", e.target.value) }),
        select([{ value: "", label: "Presets…" }, ...(w.content === "Clock" ? CLOCK_FORMATS : DATE_FORMATS).map(f => ({ value: f, label: f }))], "", v => v && set("format", v, true)))) : null,
      w.content === "Sensor" ? field("Value", sensorSelect) : null,
      w.content === "Sensor" ? h("div", { class: "two" },
        field("Decimals", number(w.decimals, v => set("decimals", clamp(v, 0, 3)), { min: 0, max: 3 })),
        h("div", { style: { alignSelf: "end", paddingBottom: "8px" } }, check("Show the unit", w.showUnit, v => set("showUnit", v)))) : null,
      w.content !== "Text" ? h("div", { class: "two" },
        field("Before", h("input", { type: "text", value: w.prefix, oninput: e => set("prefix", e.target.value) })),
        field("After", h("input", { type: "text", value: w.suffix, oninput: e => set("suffix", e.target.value) }))) : null,
      h("div", { class: "two" },
        field("Size", number(w.size, v => set("size", clamp(Math.round(v), 6, 400)), { min: 6, max: 400 })),
        field("Weight", select(["Light", "Regular", "Medium", "Bold"], w.weight, v => set("weight", v)))),
      field("Colour", colorInput(w.color, v => set("color", v), SWATCHES)),
      field("Align", h("div", { class: "row" }, ["Left", "Center", "Right"].map(a => h("button", { class: w.align === a ? "on" : "", onclick: () => set("align", a, true) }, a)))),
      h("div", { class: "two" },
        field("X", number(w.x, v => set("x", Math.round(v)))),
        field("Y", number(w.y, v => set("y", Math.round(v))))),
      field("Box width", number(w.width, v => set("width", Math.max(20, Math.round(v))), { min: 20 })),
      h("div", { class: "row" }, h("button", { onclick: () => { w.x = Math.round((screen().w - w.width) / 2); changed(); renderPanel(); } }, "Centre horizontally"),
        h("button", { onclick: () => { w.x = 0; w.width = screen().w; changed(); renderPanel(); } }, "Full width")),
      check("Badge (gradient pill behind the text)", w.badge, v => set("badge", v, true)),
      w.badge ? h("div", { class: "stack" },
        h("div", { class: "two" }, field("From", colorInput(w.badgeFrom, v => set("badgeFrom", v))), field("To", colorInput(w.badgeTo, v => set("badgeTo", v)))),
        h("div", { class: "swatches" }, BADGES.map(([a, b]) => h("button", { class: "swatch", style: { width: "44px", background: `linear-gradient(90deg, ${a}, ${b})` }, onclick: () => { w.badgeFrom = a; w.badgeTo = b; changed(); renderPanel(); } })))) : null,
      h("div", { class: "row" }, check("Hidden", w.hidden, v => set("hidden", v)), check("Locked", w.locked, v => set("locked", v))));
  }

  function removeWidget(w) {
    design.widgets = design.widgets.filter(x => x !== w);
    selected = null; changed(); render();
  }

  function useBackground(id) {
    Object.assign(design.background, { mediaId: id, x: null, y: null, width: null, startSeconds: 0, durationSeconds: null });
    selected = "bg"; tab = "background"; changed(); render();
  }

  function backgroundPanel() {
    const b = design.background, item = bgItem();
    const file = h("input", { type: "file", accept: "image/*,video/*", hidden: true, onchange: async () => {
      const id = await uploadFiles([...file.files], text => chips.replaceChildren(h("span", { class: "chip" }, text)));
      if (id) { library = await api("/library"); useBackground(id); }
    } });
    const fit = () => {
      if (!item) return;
      const { w: sw, h: sh } = screen(), k = Math.min(sw / item.width, sh / item.height);
      Object.assign(b, { width: Math.round(item.width * k), x: Math.round((sw - item.width * k) / 2), y: Math.round((sh - item.height * k) / 2) });
      changed();
    };
    return h("div", {},
      h("div", { class: "card stack" },
        h("div", { class: "row" }, h("h2", { class: "grow" }, "Background"), h("button", { onclick: () => file.click() }, "Upload…"), file,
          h("a", { href: "#/library" }, h("button", {}, "YouTube…"))),
        h("div", { class: "media-pick" },
          h("button", { class: !b.mediaId ? "on" : "", title: "Colour only", onclick: () => { b.mediaId = null; changed(); render(); }, style: { background: b.color } }, h("span", { class: "tag" }, "Colour")),
          library.map(i => h("button", { class: i.id === b.mediaId ? "on" : "", title: i.name, onclick: () => useBackground(i.id) },
            h("img", { src: `/api/library/${i.id}/thumb`, loading: "lazy", alt: "" }), h("span", { class: "tag" }, i.isImage ? "Photo" : fmtTime(i.duration))))),
        field(item ? "Colour around the picture" : "Colour", colorInput(b.color, v => { b.color = v; changed(); }, SWATCHES))),
      item ? h("div", { class: "card stack" },
        h("h3", {}, item.name),
        h("p", { class: "muted small" }, "Drag the picture on the screen to move it, scroll to zoom."),
        h("div", { class: "row" },
          h("button", { onclick: () => { Object.assign(b, { x: null, y: null, width: null }); changed(); } }, "Fill the screen"),
          h("button", { onclick: fit }, "Show it whole")),
        !item.isImage ? h("div", { class: "two" },
          field("Start (m:ss)", h("input", { type: "text", value: fmtTime(b.startSeconds), onchange: e => { const s = parseTime(e.target.value); if (!Number.isNaN(s)) { b.startSeconds = clamp(s, 0, item.duration); changed(); } } })),
          field("Loop length (s, empty = to the end)", h("input", { type: "number", min: 1, value: b.durationSeconds ?? "", onchange: e => { b.durationSeconds = e.target.value === "" ? null : Math.max(1, Number(e.target.value)); changed(); } }))) : null,
        !item.isImage ? h("p", { class: "muted small" }, `Video: ${fmtTime(item.duration)} · ${item.width}×${item.height}. A new section is prepared once (a few seconds), then plays in a loop.`) : null) : null);
  }

  // ------------------------------------------------------------ drawing

  function startDrawing() {
    const { w: sw, h: sh } = screen();
    const canvas = h("canvas", { class: "draw", width: sw, height: sh });
    const ctx = canvas.getContext("2d");
    drawing = { canvas, ctx, color: "#FFFFFF", size: 6, eraser: false, dirty: false };
    if (design.hasDrawing) { const img = new Image(); img.onload = () => ctx.drawImage(img, 0, 0, sw, sh); img.src = "/api/drawing?t=" + Date.now(); }
    let last = null;
    const point = e => { const r = canvas.getBoundingClientRect(); return { x: (e.clientX - r.left) * sw / r.width, y: (e.clientY - r.top) * sh / r.height }; };
    canvas.addEventListener("pointerdown", e => { canvas.setPointerCapture(e.pointerId); last = point(e); stroke(last, last); });
    canvas.addEventListener("pointermove", e => { if (!last) return; const p = point(e); stroke(last, p); last = p; });
    canvas.addEventListener("pointerup", () => (last = null));
    const stroke = (a, b) => {
      ctx.globalCompositeOperation = drawing.eraser ? "destination-out" : "source-over";
      ctx.strokeStyle = drawing.color; ctx.lineWidth = drawing.size; ctx.lineCap = ctx.lineJoin = "round";
      ctx.beginPath(); ctx.moveTo(a.x, a.y); ctx.lineTo(b.x + 0.01, b.y); ctx.stroke();
      drawing.dirty = true;
    };
    layer.replaceChildren(canvas);
  }
  function stopDrawing() {
    if (!drawing) return;
    drawing = null; layer.replaceChildren(); drawLayer();
  }
  async function saveDrawing() {
    const blob = await new Promise(r => drawing.canvas.toBlob(r, "image/png"));
    design = await api("/drawing", { method: "PUT", body: blob, raw: true });
    drawing.dirty = false;
    // The server now draws it: clear the overlay copy so it is not shown twice.
    toast("Drawing saved: it is on the cooler now.");
  }
  function drawPanel() {
    if (!drawing) startDrawing();
    return h("div", { class: "card stack" },
      h("h2", {}, "Draw on the screen"),
      h("p", { class: "muted small" }, "Draw directly on the screen preview, then save: it is shown over the background, under the text."),
      field("Brush colour", colorInput(drawing.color, v => { drawing.color = v; drawing.eraser = false; renderPanel(); }, SWATCHES)),
      field("Brush size", range(1, 40, drawing.size, v => (drawing.size = v))),
      h("div", { class: "row" },
        h("button", { class: drawing.eraser ? "on" : "", onclick: () => { drawing.eraser = !drawing.eraser; renderPanel(); } }, "Eraser"),
        h("button", { onclick: () => { drawing.ctx.clearRect(0, 0, drawing.canvas.width, drawing.canvas.height); drawing.dirty = true; } }, "Clear"),
        h("button", { class: "primary", onclick: saveDrawing }, "Save drawing"),
        design.hasDrawing ? h("button", { class: "danger", onclick: async () => { design = await api("/drawing", { method: "DELETE" }); drawing.ctx.clearRect(0, 0, drawing.canvas.width, drawing.canvas.height); renderPanel(); } }, "Delete drawing") : null));
  }

  load().catch(err => toast(err.message));
  return {
    dispose() {
      disposed = true; clearTimeout(frameTimer); clearInterval(sensorTimer);
      document.removeEventListener("keydown", onKey); window.removeEventListener("resize", onResize);
    },
  };
}

/** Uploads files with progress; resolves with the library id of the first one once it is analysed. */
async function uploadFiles(files, onText) {
  const form = new FormData();
  files.forEach(f => form.append("files", f, f.name));
  const started = await new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open("POST", "/api/library");
    xhr.upload.onprogress = e => e.lengthComputable && onText?.(`Uploading… ${Math.round(e.loaded / e.total * 100)} %`);
    xhr.onload = () => xhr.status < 300 ? resolve(JSON.parse(xhr.responseText)) : reject(new Error(JSON.parse(xhr.responseText || "{}").error ?? xhr.statusText));
    xhr.onerror = () => reject(new Error("Upload failed."));
    xhr.send(form);
  }).catch(err => { toast(err.message); return null; });
  if (!started?.length) return null;
  onText?.("Analysing…");
  return await waitForJob(started[0].id, onText);
}

async function waitForJob(id, onText) {
  for (;;) {
    await new Promise(r => setTimeout(r, 700));
    const job = (await api("/jobs")).find(j => j.id === id);
    if (!job) return null;
    if (job.state === "running") { onText?.(job.text + (job.fraction != null ? ` ${Math.round(job.fraction * 100)} %` : "")); continue; }
    if (job.state === "failed") { toast(job.error ?? "Failed."); return null; }
    return job.resultId;
  }
}

// ---------------------------------------------------------------------------------------------- library

function Library(root) {
  let disposed = false;
  const grid = h("div", { class: "grid" });
  const jobsBox = h("div", { class: "card jobs", hidden: true });
  const file = h("input", { type: "file", accept: "image/*,video/*", multiple: true, hidden: true, onchange: () => upload([...file.files]) });
  const drop = h("div", { class: "dropzone" }, "Drop pictures and videos here, or ", h("a", { href: "#", onclick: e => { e.preventDefault(); file.click(); } }, "choose files"), file);
  drop.addEventListener("dragover", e => { e.preventDefault(); drop.classList.add("drop"); });
  drop.addEventListener("dragleave", () => drop.classList.remove("drop"));
  drop.addEventListener("drop", e => { e.preventDefault(); drop.classList.remove("drop"); upload([...e.dataTransfer.files]); });

  const yt = youtubeCard(() => refreshJobs());
  root.append(
    h("div", { class: "head" }, h("div", {}, h("h1", {}, "Library"), h("div", { class: "muted" }, "Everything you can put on the screen. Videos play when you point at them."))),
    h("div", { class: "card stack" }, h("h2", {}, "Add files"), drop), yt,
    jobsBox, h("div", { style: { height: "14px" } }), grid);

  async function upload(files) {
    if (!files.length) return;
    const bar = h("div", { class: "card" }, "Uploading…");
    root.insertBefore(bar, grid);
    await uploadFiles(files, text => (bar.textContent = text));
    bar.remove();
    load();
  }

  async function load() {
    const [items, design] = await Promise.all([api("/library"), api("/design")]);
    grid.replaceChildren(...items.map(i => card(i, design.background.mediaId === i.id)));
    if (!items.length) grid.replaceChildren(h("p", { class: "muted" }, "No media yet."));
  }

  function card(i, active) {
    const thumb = h("div", { class: "thumb" }, h("img", { src: `/api/library/${i.id}/thumb`, loading: "lazy", alt: "" }),
      h("span", { class: "badge" + (i.isImage ? "" : " video") }, i.isImage ? "PHOTO" : "VIDEO " + fmtTime(i.duration)));
    if (!i.isImage) {
      let video;
      thumb.addEventListener("mouseenter", () => { video = h("video", { src: `/api/library/${i.id}/file`, muted: true, autoplay: true, playsInline: true });
        // Only the item's own part, in a loop. A YouTube section keeps its original timestamps; some browsers
        // show those (the file then "starts" at fileStart), others count from 0.
        let from = 0, to = Infinity;
        video.addEventListener("loadedmetadata", () => {
          const absolute = i.fileStart > 0.5 && video.duration >= i.fileStart + i.clipStart + i.duration - 0.25;
          from = (absolute ? i.fileStart : 0) + (i.clipStart ?? 0);
          to = from + i.duration;
          video.currentTime = from;
        });
        video.addEventListener("timeupdate", () => { if (video.currentTime >= to || video.currentTime < from - 0.5) video.currentTime = from; });
        video.addEventListener("ended", () => { video.currentTime = from; video.play(); });
        thumb.append(video); });
      thumb.addEventListener("mouseleave", () => { video?.remove(); video = null; });
    }
    return h("div", { class: "media" }, thumb,
      h("div", { class: "info" }, h("div", { class: "title", title: i.name }, i.name),
        h("div", { class: "muted small" }, `${i.width}×${i.height} · ${fmtBytes(i.size)}${active ? " · on the screen" : ""}`)),
      h("div", { class: "actions" },
        h("button", { class: active ? "on" : "primary", onclick: async () => {
          const design = await api("/design");
          Object.assign(design.background, { mediaId: i.id, x: null, y: null, width: null, startSeconds: 0, durationSeconds: null });
          await api("/design", { method: "PUT", body: design }); toast("On the screen now."); load();
        } }, active ? "On the screen" : "Use as background"),
        h("button", { onclick: async () => { const name = prompt("Name", i.name); if (name) { await api(`/library/${i.id}`, { method: "PATCH", body: { name } }); load(); } } }, "Rename"),
        h("button", { class: "danger", onclick: async () => { if (confirm(`Delete “${i.name}”?`)) { await api(`/library/${i.id}`, { method: "DELETE" }); load(); } } }, "Delete")));
  }

  async function refreshJobs() {
    if (disposed) return;
    const jobs = await api("/jobs").catch(() => []);
    jobsBox.hidden = !jobs.length;
    jobsBox.replaceChildren(h("h2", {}, "In progress"), ...jobs.map(j => h("div", { class: "job" },
      h("div", { class: "row" }, h("span", { class: "grow" }, `${j.title}: `, j.state === "failed" ? h("span", { class: "error" }, j.error) : j.text),
        j.state === "running" ? h("button", { class: "icon", onclick: () => api(`/jobs/${j.id}`, { method: "DELETE" }) }, "Cancel") : null),
      h("div", { class: "bar" + (j.state === "running" && j.fraction == null ? " busy" : "") }, h("i", { style: { width: Math.round((j.fraction ?? 0) * 100) + "%" } })))));
    if (jobs.some(j => j.state === "done" && !seen.has(j.id))) { jobs.forEach(j => seen.add(j.id)); load(); }
  }
  const seen = new Set();
  const timer = setInterval(refreshJobs, 1000);
  load().catch(err => toast(err.message));
  refreshJobs();
  return { dispose() { disposed = true; clearInterval(timer); } };
}

/**
 * YouTube import: paste a link, choose the section on a timeline, check its first and last frames
 * (square, as on the screen) and watch it loop, then import it in the best quality.
 */
function youtubeCard(onStarted) {
  let video = null, url = "", start = 0, length = 15, previewId = null, previewFor = "", job = null;
  const MAX = 300;
  const fmt = s => {
    const t = Math.max(0, Math.round(s * 10) / 10), m = Math.floor(t / 60), sec = Math.round((t - m * 60) * 10) / 10;
    const whole = Math.floor(sec), tenth = Math.round((sec - whole) * 10);
    return `${m}:${String(whole).padStart(2, "0")}${tenth ? "." + tenth : ""}`;
  };
  const end = () => Math.min(video.durationSeconds, start + length);
  const key = () => `${url}|${start}|${length}`;

  const input = h("input", { type: "url", placeholder: "Paste a YouTube link…", onkeydown: e => e.key === "Enter" && lookup() });
  input.addEventListener("input", debounce(() => /youtu\.?be/i.test(input.value) && input.value.trim() !== url && lookup(), 400));
  const lookupButton = h("button", { class: "primary", onclick: () => lookup() }, "Look up");
  const body = h("div", { class: "stack" });

  async function lookup() {
    if (!input.value.trim()) return;
    url = input.value.trim();
    lookupButton.disabled = true;
    body.replaceChildren(h("p", { class: "muted" }, "Reading the video… (a few seconds)"));
    try {
      video = await api("/youtube/probe", { method: "POST", body: { url } });
      start = 0;
      length = Math.min(15, Math.max(1, Math.floor(video.durationSeconds)));
      previewId = null;
      render();
    } catch (err) {
      video = null;
      body.replaceChildren(h("p", { class: "error" }, err.message));
    } finally { lookupButton.disabled = false; }
  }

  // ------------------------------------------------------------ the parts that change with the section

  const range = h("div", { class: "range" }, h("span", { class: "handle l" }), h("span", { class: "handle r" }));
  const track = h("div", { class: "track" });
  const timeline = h("div", { class: "timeline" }, track, range);
  const readout = h("div", { class: "readout" });
  const startBox = h("input", { type: "text" });
  const lengthBox = h("input", { type: "number", min: 1, max: MAX, step: 0.5 });
  const firstFrame = frameBox("First frame");
  const lastFrame = frameBox("Last frame");
  const loopBox = h("div", { class: "frame loop" }, h("span", { class: "frame-label" }, "Loop preview"));
  const loopButton = h("button", { onclick: () => previewLoop() }, "▶ Preview the loop");
  const status = h("div", { class: "stack" });

  function frameBox(label) {
    const img = h("img", { alt: "" });
    const note = h("span", { class: "frame-note" }, "");
    const box = h("div", { class: "frame" }, img, h("span", { class: "frame-label" }, label), note);
    box.show = (t) => {
      note.textContent = "Loading…";
      img.style.opacity = ".35";
      const src = `/api/youtube/frame?url=${encodeURIComponent(url)}&t=${t.toFixed(2)}`;
      const probe = new Image();
      probe.onload = () => { if (box.wanted === src) { img.src = src; img.style.opacity = "1"; note.textContent = fmt(t); } };
      probe.onerror = () => { if (box.wanted === src) note.textContent = "Not available"; };
      box.wanted = src;
      probe.src = src;
    };
    return box;
  }

  const loadFrames = debounce(() => {
    firstFrame.show(start);
    // The last frame shown before the loop starts over (one frame before the end).
    lastFrame.show(Math.max(start, end() - 0.05));
  }, 600);

  function update(changedFrames = true) {
    if (!video) return;
    const d = video.durationSeconds;
    range.style.left = (start / d * 100) + "%";
    range.style.width = (Math.max(0.3, (end() - start) / d * 100)) + "%";
    startBox.value = fmt(start);
    lengthBox.value = String(Math.round((end() - start) * 10) / 10);
    const mb = video.bytesPerSecond ? ` · ≈ ${Math.max(1, Math.round(video.bytesPerSecond * (end() - start) / 1048576))} MB` : "";
    readout.replaceChildren(
      h("b", {}, `${fmt(start)} → ${fmt(end())}`),
      h("span", {}, ` · ${Math.round((end() - start) * 10) / 10} s of ${fmt(d)}${mb}`));
    if (changedFrames) loadFrames();
    loopButton.textContent = previewId && previewFor === key() ? "▶ Watch again" : previewId ? "▶ Preview the new section" : "▶ Preview the loop";
  }

  function set(newStart, newLength) {
    const d = video.durationSeconds;
    length = clamp(Math.round(newLength * 10) / 10, 1, Math.min(MAX, d));
    start = clamp(Math.round(newStart * 10) / 10, 0, Math.max(0, d - length));
    update();
  }

  // Timeline: drag the section to move it, its edges to change start or end; a click elsewhere moves it there.
  timeline.addEventListener("pointerdown", e => {
    if (!video) return;
    e.preventDefault();
    timeline.setPointerCapture(e.pointerId);
    const rect = timeline.getBoundingClientRect();
    const at = ev => clamp((ev.clientX - rect.left) / rect.width, 0, 1) * video.durationSeconds;
    const mode = e.target.classList.contains("l") ? "start" : e.target.classList.contains("r") ? "end" : e.target === range ? "move" : "jump";
    const from = { t: at(e), start, end: end() };
    if (mode === "jump") set(at(e) - length / 2, length);
    const move = ev => {
      const t = at(ev);
      if (mode === "start") set(Math.min(t, from.end - 1), from.end - Math.min(t, from.end - 1));
      else if (mode === "end") set(from.start, Math.max(1, t - from.start));
      else set((mode === "move" ? from.start : start) + (mode === "move" ? t - from.t : 0), length);
    };
    const up = () => { timeline.removeEventListener("pointermove", move); timeline.removeEventListener("pointerup", up); };
    timeline.addEventListener("pointermove", move);
    timeline.addEventListener("pointerup", up);
  });
  startBox.addEventListener("change", () => { const s = parseTime(startBox.value); Number.isNaN(s) ? update(false) : set(s, length); });
  lengthBox.addEventListener("change", () => set(start, Number(lengthBox.value) || length));

  async function previewLoop() {
    if (previewId && previewFor === key()) return playPreview();
    loopButton.disabled = true;
    loopBox.querySelector("video")?.remove();
    loopBox.dataset.note = "";
    const note = h("span", { class: "frame-note" }, "Preparing a light copy…");
    loopBox.append(note);
    try {
      const p = await api("/youtube/preview", { method: "POST", body: { url, start, length } });
      previewId = p.id; previewFor = key();
      loopBox.preview = p;
      note.remove();
      playPreview();
    } catch (err) {
      note.textContent = err.message;
    } finally { loopButton.disabled = false; update(false); }
  }

  function playPreview() {
    const p = loopBox.preview;
    loopBox.querySelector("video")?.remove();
    // The server cut exactly the section (starting at 0): the whole file is the loop.
    const v = h("video", { src: `/api/youtube/preview/${p.id}`, muted: true, playsInline: true, autoplay: true, loop: true });
    loopBox.prepend(v);
  }

  async function importIt(apply) {
    try {
      const started = await api("/youtube/import", { method: "POST", body: { url, start, length, apply } });
      job = started.id;
      onStarted?.();
      follow(apply);
    } catch (err) { toast(err.message); }
  }

  async function follow(apply) {
    const bar = h("i", { style: { width: "0%" } });
    const barBox = h("div", { class: "bar busy" }, bar);
    const title = h("b", {}, "Downloading in the best quality…");
    const detail = h("div", { class: "muted small" });
    const cancel = h("button", { class: "icon", onclick: () => api(`/jobs/${job}`, { method: "DELETE" }) }, "Cancel");
    status.replaceChildren(h("div", { class: "row" }, h("div", { class: "grow" }, title), cancel), barBox, detail);
    for (;;) {
      await new Promise(r => setTimeout(r, 700));
      const j = (await api("/jobs").catch(() => [])).find(x => x.id === job);
      if (!j) return status.replaceChildren();
      if (j.state === "running") {
        title.textContent = j.text;
        detail.textContent = j.detail ?? "";
        barBox.classList.toggle("busy", j.fraction == null);
        bar.style.width = Math.round((j.fraction ?? 0) * 100) + "%";
        continue;
      }
      if (j.state === "failed") return status.replaceChildren(h("p", { class: "error" }, j.error ?? "The import failed."));
      if (j.state === "cancelled") return status.replaceChildren(h("p", { class: "muted" }, "Cancelled."));
      return status.replaceChildren(h("p", { class: "ok" }, apply ? "Imported, and on the screen now." : "Imported into the library below."),
        apply ? h("a", { href: "#/studio" }, "Open the Studio") : null);
    }
  }

  function render() {
    const d = video.durationSeconds;
    const lengths = [5, 10, 15, 30, 60].filter(l => l <= d);
    body.replaceChildren(
      h("div", { class: "yt-preview" }, video.thumbnailUrl ? h("img", { src: video.thumbnailUrl, alt: "" }) : h("div"),
        h("div", { class: "stack", style: { gap: "2px" } }, h("b", {}, video.title),
          h("div", { class: "muted small" }, `${video.channel} · ${fmt(d)}`),
          h("div", { class: "small", style: { color: "var(--accent)" } }, "Best available: " + video.best),
          video.onlyHdr ? h("div", { class: "small", style: { color: "var(--warm)" } }, "Only an HDR version exists: colours may look washed out.") : null)),
      h("div", { class: "stack", style: { gap: "6px" } },
        h("div", { class: "row" }, h("h3", { class: "grow" }, "Choose the part to loop"), readout),
        timeline,
        h("div", { class: "row small muted", style: { justifyContent: "space-between" } }, h("span", {}, "0:00"), h("span", {}, fmt(d / 2)), h("span", {}, fmt(d)))),
      h("div", { class: "two" },
        field("Start (m:ss)", h("div", { class: "row" },
          h("button", { class: "icon", title: "1 second earlier", onclick: () => set(start - 1, length) }, "−1 s"), h("div", { class: "grow" }, startBox),
          h("button", { class: "icon", title: "1 second later", onclick: () => set(start + 1, length) }, "+1 s"))),
        field("Length (seconds)", h("div", { class: "row" }, h("div", { class: "grow" }, lengthBox),
          ...lengths.map(l => h("button", { class: "icon", onclick: () => set(start, l) }, `${l}`))))),
      h("div", { class: "frames" }, firstFrame, lastFrame, loopBox),
      h("p", { class: "muted small" }, "Cropped square, as on your screen. When the last frame flows into the first, the loop looks seamless."),
      h("div", { class: "row" }, loopButton, h("span", { class: "grow" }),
        h("button", { onclick: () => importIt(false) }, "Import to the library"),
        h("button", { class: "primary", onclick: () => importIt(true) }, "Import and show on the screen")),
      status);
    update();
  }

  return h("div", { class: "card stack" }, h("h2", {}, "YouTube"),
    h("div", { class: "row" }, h("div", { class: "grow" }, input), lookupButton), body);
}

// ---------------------------------------------------------------------------------------------- ARGB

function Argb(root) {
  let data = null, profile = null, disposed = false;
  const body = h("div");
  root.append(h("div", { class: "head" },
    h("div", {}, h("h1", {}, "ARGB"), h("div", { class: "muted" }, "Every RGB device OpenRGB detects in this server: sync them or give each its own look.")),
    h("button", { onclick: async () => { await api("/lighting/refresh", { method: "POST" }); load(); } }, "Detect again")), body);

  const push = debounce(() => api("/lighting", { method: "PUT", body: profile }).catch(err => toast(err.message)), 100);
  const deviceSettings = d => {
    let s = profile.devices.find(x => x.key === d.key);
    if (!s) { s = { key: d.key, name: d.name, enabled: true, inSync: true, touched: false, setting: { effect: "Static", color: "#00E5FF", speed: 50, brightness: 100 }, zones: [] }; profile.devices.push(s); }
    return s;
  };
  const zoneSettings = (s, name) => { let z = s.zones.find(x => x.zone === name); if (!z) { z = { zone: name }; s.zones.push(z); } return z; };
  const usesColor = (effect, devices) => effect === "Static" || devices.some(d => { const m = d.modes.find(x => x.name.toLowerCase() === effect.toLowerCase()); return !m || m.color; });
  const usesSpeed = (effect, devices) => devices.some(d => d.modes.find(x => x.name.toLowerCase() === effect.toLowerCase())?.hasSpeed);

  async function load() {
    data = await api("/lighting");
    profile = data.profile;
    render();
  }

  function look(setting, effects, devices, onChange) {
    const set = (k, v, re) => { setting[k] = v; onChange(); if (re) render(); };
    return h("div", { class: "stack" },
      field("Effect", select(effects, setting.effect, v => set("effect", v, true))),
      usesColor(setting.effect, devices) ? field("Colour", colorInput(setting.color, v => set("color", v), SWATCHES)) : null,
      usesSpeed(setting.effect, devices) || ["Rainbow", "Spectrum Cycle", "Breathing", "Flashing"].includes(setting.effect) ? field("Speed", range(0, 100, setting.speed, v => set("speed", v))) : null,
      setting.effect !== "Off" ? field("Brightness", range(0, 100, setting.brightness, v => set("brightness", v))) : null);
  }

  function render() {
    if (!data) return;
    const devices = data.devices;
    const sync = h("div", { class: "card stack" },
      h("div", { class: "row" }, h("h2", { class: "grow" }, "All devices"), check("Sync", profile.sync, v => { profile.sync = v; push(); render(); })),
      profile.sync ? look(profile.syncSetting, data.syncEffects, devices, push) : h("p", { class: "muted small" }, "Turn sync on to give every device in sync the same look."),
      h("div", { class: "swatches" }, SWATCHES.map(c => h("button", { class: "swatch", style: { background: c }, title: c, onclick: () => { profile.sync = true; profile.syncSetting.color = c; if (profile.syncSetting.effect === "Off") profile.syncSetting.effect = "Static"; push(); render(); } }))));
    const name = h("input", { type: "text", placeholder: "Profile name" });
    const profiles = h("div", { class: "card stack" }, h("h2", {}, "Profiles"),
      data.profiles.length ? h("div", { class: "stack" }, data.profiles.map(p => h("div", { class: "row" }, h("span", { class: "grow" }, p),
        h("button", { onclick: async () => { await api(`/lighting/profiles/${encodeURIComponent(p)}/load`, { method: "POST" }); toast(`“${p}” applied.`); setTimeout(load, 300); } }, "Apply"),
        h("button", { class: "icon danger", onclick: async () => { await api(`/lighting/profiles/${encodeURIComponent(p)}`, { method: "DELETE" }); load(); } }, "✕")))) : h("p", { class: "muted small" }, "Save the current lighting to switch back to it later."),
      h("div", { class: "row" }, h("div", { class: "grow" }, name), h("button", { onclick: async () => { if (!name.value.trim()) return; await push.now(); await api("/lighting/profiles", { method: "POST", body: { name: name.value } }); toast("Profile saved."); load(); } }, "Save")),
      h("button", { onclick: async () => { const r = await api("/lighting/save-to-devices", { method: "POST" }); toast(r.saved ? `Stored in ${r.saved} device(s): shown from power-on.` : "None of these devices can store their lighting; this server restores it at start-up."); } }, "Save to devices (power-on)"));

    const cards = devices.map(d => {
      const s = deviceSettings(d);
      const following = profile.sync && s.inSync;
      const touched = () => { s.touched = true; push(); };
      return h("div", { class: "card device" + (s.enabled ? "" : " off") },
        h("div", { class: "top" },
          h("div", { class: "grow" }, h("div", { class: "type" }, (d.type === "Dram" ? "RAM" : d.type).toUpperCase()), h("h2", {}, d.name), h("div", { class: "muted small" }, `${d.vendor} · ${d.ledCount} LEDs`)),
          check("On", s.enabled, v => { s.enabled = v; push(); render(); })),
        s.enabled && profile.sync ? check("Follow sync", s.inSync, v => { s.inSync = v; push(); render(); }) : null,
        s.enabled && following ? h("div", { class: "following" }, "Following the sync look") : null,
        s.enabled && !following ? look(s.setting, d.effects, [d], touched) : null,
        d.zones.length ? h("div", { class: "zones" }, d.zones.map(z => {
          const zs = zoneSettings(s, z.name);
          const label = h("input", { type: "text", value: zs.label ?? "", placeholder: z.name === "Unknown" ? "All LEDs" : z.name, onchange: () => { zs.label = label.value.trim() || null; push(); } });
          return h("div", { class: "zone" },
            h("div", { class: "stack", style: { gap: "4px" } }, label, h("span", { class: "muted small" }, `${z.name} · ${z.ledCount} LEDs`)),
            z.resizable ? field("LEDs", number(zs.ledCount ?? z.ledCount, v => { zs.ledCount = clamp(Math.round(v), z.ledsMin, z.ledsMax); touched(); setTimeout(load, 600); }, { min: z.ledsMin, max: z.ledsMax })) : h("span"),
            !following && d.zones.length > 1 && s.setting.effect === "Static" ? colorInput(zs.color ?? s.setting.color, v => { zs.color = v; touched(); }) : h("span"));
        })) : null);
    });

    body.replaceChildren(
      !data.connected ? h("div", { class: "card error" }, data.status) : h("p", { class: "muted" }, data.status),
      h("div", { class: "argb" }, h("div", {}, sync, profiles), h("div", { class: "devices" }, cards)));
  }

  load().catch(err => toast(err.message));
  return { dispose() { disposed = true; push.now(); } };
}

// ---------------------------------------------------------------------------------------------- settings

function Settings(root) {
  let settings = null;
  const body = h("div");
  root.append(h("div", { class: "head" }, h("div", {}, h("h1", {}, "Settings"))), body);
  const save = debounce(async patch => { settings = await api("/settings", { method: "PUT", body: patch }); }, 150);

  async function load() { settings = await api("/settings"); render(); }
  function render() {
    const s = status?.screen;
    const current = h("input", { type: "password", placeholder: "Current password", autocomplete: "current-password" });
    const next = h("input", { type: "password", placeholder: "New password (empty removes it)", autocomplete: "new-password" });
    body.replaceChildren(h("div", { class: "two", style: { alignItems: "start" } },
      h("div", { class: "card stack" },
        h("h2", {}, "Screen"),
        h("p", { class: "muted" }, s?.connected ? `${s.name} · ${s.fps} frames per second` : (s?.error ?? "Not connected")),
        check("Screen on", settings.on, v => save({ on: v })),
        field("Brightness", range(0, 100, settings.brightness, v => save({ brightness: v }))),
        field("Rotation (how the cooler is mounted)", select([0, 90, 180, 270].map(r => ({ value: String(r), label: r ? `${r}°` : "Upright" })), String(settings.rotation), v => save({ rotation: Number(v) }))),
        field("Frames per second", range(5, 30, settings.framesPerSecond, v => save({ framesPerSecond: v }))),
        check("Temperatures in Fahrenheit (°F)", settings.fahrenheit, v => save({ fahrenheit: v }))),
      h("div", { class: "card stack" },
        h("h2", {}, "Access"),
        h("p", { class: "muted small" }, settings.hasPassword ? "This page asks for a password." : "Anyone on your network can open this page. Set a password to protect it."),
        settings.hasPassword ? current : null, next,
        h("button", { onclick: async () => {
          try {
            settings = await api("/settings/password", { method: "POST", body: { current: current.value, password: next.value } });
            toast(settings.hasPassword ? "Password set." : "Password removed."); render();
          } catch (err) { toast(err.message); }
        } }, "Save password"),
        settings.hasPassword ? h("button", { onclick: async () => { await api("/auth", { method: "DELETE" }); location.reload(); } }, "Sign out") : null,
        h("h2", { style: { marginTop: "10px" } }, "About"),
        h("p", { class: "muted small" }, `Unbounded Panorama 360 v${status?.version ?? ""}, container edition. Open source (MIT).`),
        status?.proxmox ? h("p", { class: "small" }, h("span", { class: "dot " + (status.proxmox.working ? "on" : "warn") }), status.proxmox.working ? "Proxmox statistics connected (read-only)" : `Proxmox: ${status.proxmox.error}`) : null)));
  }
  load().catch(err => toast(err.message));
  return { dispose() { save.now(); } };
}
