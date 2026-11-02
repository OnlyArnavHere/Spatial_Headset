"use strict";
// The agent window. Everything shown comes from api.poll(); every control calls one Bridge method (gui/web_app.py).

const ICONS = {
  usb: '<path d="M12 21V5M12 5l-2.5 3M12 5l2.5 3M12 16l-4.5-3V10M12 14l4.5-3V9"/><circle cx="12" cy="21" r="1.2" fill="currentColor"/><rect x="6" y="8.5" width="3" height="2" rx=".5"/><circle cx="16.5" cy="8.5" r="1.2"/>',
  wifi: '<path d="M2.5 9a14 14 0 0 1 19 0M5.5 12.5a9.5 9.5 0 0 1 13 0M8.6 16a5 5 0 0 1 6.8 0"/><circle cx="12" cy="19" r=".8" fill="currentColor"/>',
  power: '<path d="M12 3.5v8"/><path d="M7 6.5a7.5 7.5 0 1 0 10 0"/>',
  qr: '<rect x="4" y="4" width="6" height="6" rx="1.2"/><rect x="14" y="4" width="6" height="6" rx="1.2"/><rect x="4" y="14" width="6" height="6" rx="1.2"/><path d="M14 14h2.5v2.5H14zM17.5 17.5H20V20h-2.5zM14 20h1M20 14v1"/>',
  sliders: '<path d="M4 7h10M18 7h2M4 17h4M12 17h8"/><circle cx="16" cy="7" r="2"/><circle cx="10" cy="17" r="2"/>',
  headset: '<path d="M3 10.5c0-1.4 1.1-2.5 2.5-2.5h13c1.4 0 2.5 1.1 2.5 2.5v4c0 1.4-1.1 2.5-2.5 2.5h-3l-1.8-2.2a2.2 2.2 0 0 0-3.4 0L8.5 17h-3A2.5 2.5 0 0 1 3 14.5z"/>',
};

function icon(name) {
  return `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">${ICONS[name] || ""}</svg>`;
}
function paintIcons(root) {
  root.querySelectorAll("[data-icon]").forEach(el => {
    if (el.dataset.painted === el.dataset.icon) return;
    el.querySelectorAll("svg").forEach(s => s.remove());
    el.insertAdjacentHTML("afterbegin", icon(el.dataset.icon));
    el.dataset.painted = el.dataset.icon;
  });
}
const $ = id => document.getElementById(id);
const esc = s => String(s).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
function setText(id, text) { const el = $(id); if (el.textContent !== text) el.textContent = text; }

let api = null;
let snap = null;
let logSeq = 0;
let typing = false;      // the pair widget shows the code field instead of only the QR
let confirming = "";     // the headset whose Forget is waiting for a second answer
let lastHeadsKey = "";
let quitArmed = 0;
let toastTimer = 0;

function toast(text) {
  if (!text) return;
  const t = $("toast");
  t.hidden = true;
  t.textContent = text;
  void t.offsetWidth; // restart the entrance
  t.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { t.hidden = true; }, 3800);
}

// ---------- rendering ----------

function modeOf(set) { return set.cast_only ? "cast" : set.allow_control ? "control" : "view"; }
const MODE_NOTE = {
  view: "The headset can only watch the windows you share.",
  control: "The headset can ask to use the mouse and keyboard. You approve it here each time.",
  cast: "The headset can watch, and play, pause, seek or change the volume.",
};

function render(s) {
  snap = s;
  renderStatus(s);
  const mode = modeOf(s.settings);
  document.querySelectorAll("[data-mode]").forEach(b => b.setAttribute("aria-checked", String(b.dataset.mode === mode)));
  setText("mode-note", MODE_NOTE[mode]);
  renderToggles(s);
  renderPairing(s.pair);
  renderHeads(s);
  renderSheet(s);
  logSeq = s.log_seq;
}

function renderStatus(s) {
  const st = s.status.state;
  const live = s.connected || [];
  const set = s.settings;
  let state = "off", title = "Off", sub = "Turn on so your headset can connect.", button = "Turn on";
  if (st === "starting") {
    state = "waiting"; title = "Starting"; sub = "Getting the screen sharing ready."; button = "Turn off";
  } else if (st === "running" && live.length) {
    state = "live";
    title = live.length === 1 ? live[0].name : `${live.length} headsets`;
    sub = live.length === 1 ? `Connected over ${live[0].link}.` : "Connected.";
    button = "Turn off";
  } else if (st === "running") {
    state = "waiting"; title = "Ready";
    sub = s.pairs.length ? "Waiting for your headset to connect." : "Pair a headset below to get started.";
    button = "Turn off";
  } else if (st === "error") {
    state = "error"; title = "Stopped"; sub = s.status.message || "Something went wrong. Turn it on to try again.";
  }
  const d = s.download;
  if (d) {
    title = "Getting ready";
    sub = d.percent == null ? `Downloading ${d.name}, ${d.mb} MB so far.` : `Downloading ${d.name}.`;
  } else if (s.permission_note && !s.permissions) {
    sub = s.permission_note;
  }
  const w = $("w-status");
  if (w.dataset.state !== state) w.dataset.state = state;
  setText("st-title", title);
  setText("st-sub", sub);
  $("dl").hidden = !d;
  if (d) {
    const bar = $("dl-bar");
    bar.classList.toggle("indet", d.percent == null);
    bar.style.width = d.percent == null ? "" : d.percent + "%";
  }
  const link = live.length === 1 ? live[0].link : set.wired ? "USB cable" : set.headset_address ? set.headset_address : "Wi-Fi";
  const chip = $("st-link");
  const chipIcon = link === "USB cable" ? "usb" : "wifi";
  if (chip.dataset.text !== link || chip.dataset.icon !== chipIcon) {
    chip.innerHTML = icon(chipIcon) + esc(link);
    chip.dataset.text = link;
    chip.dataset.icon = chipIcon;
  }
  const power = $("power");
  setText("power", button);
  power.className = "btn " + (button === "Turn on" ? "ink" : "plain");
  power.disabled = st === "starting";
}

function renderToggles(s) {
  document.querySelectorAll("[data-setting]").forEach(box => {
    const name = box.dataset.setting;
    const value = name === "start_at_login" ? s.login.enabled : !!s.settings[name];
    if (box.checked !== value) box.checked = value;
    box.closest(".w").classList.toggle("on", value);
  });
  setText("wired-value", s.settings.wired ? "On. No Wi-Fi needed." : "Off");
  const login = document.querySelector('[data-setting="start_at_login"]');
  login.disabled = !s.login.available;
  $("login-w").classList.toggle("disabled", !s.login.available);
  setText("login-value", !s.login.available ? (s.login.message || "Not available") : s.login.enabled ? "On, in the tray" : "Off");
}

function renderPairing(p) {
  const active = !!p && !p.finished;
  const done = !!p && p.finished;
  $("pair-idle").hidden = !!p;
  $("pair-live").hidden = !p;
  if (!p) return;
  const showQr = active && !!p.qr && !typing;
  $("qr").hidden = !showQr;
  if (showQr && $("qr").dataset.key !== p.qr_key) {
    $("qr").innerHTML = p.qr; // SVG drawn by the app itself (qr_image.py), not page input
    $("qr").dataset.key = p.qr_key;
  }
  $("pair-spin").hidden = !p.searching || done;
  let prompt = p.prompt;
  if (done) prompt = p.ok ? "Paired" : "Pairing stopped";
  else if (showQr) prompt = "Look at this code with the headset";
  else if (typing && p.searching) prompt = "Type the code the headset shows";
  setText("pair-prompt", prompt);
  const status = $("pair-status");
  setText("pair-status", p.status || "");
  status.className = "pair-status " + (p.tone || "");
  $("code-form").hidden = !active || !typing;
  $("pair-switch").hidden = !active;
  setText("pair-switch", typing ? "Show QR code instead" : "Type a code instead");
  setText("pair-cancel", done ? "Done" : "Cancel");
}

function shortDate(text) {
  const d = new Date(text);
  return isNaN(d) ? text : d.toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" });
}

function renderHeads(s) {
  const live = new Set((s.connected || []).map(c => c.name));
  const key = JSON.stringify([s.pairs, [...live], confirming, s.pairs_error]);
  if (key === lastHeadsKey) return;
  lastHeadsKey = key;
  if (confirming && !s.pairs.some(p => p.name === confirming)) confirming = "";
  $("heads").innerHTML = s.pairs.length ? s.pairs.map(p => {
    const name = esc(p.name);
    if (p.name === confirming) {
      return `<li class="head confirm"><div class="head-icon">${icon("headset")}</div>
        <div class="head-main"><div class="head-name">Forget ${name}?</div>
        <div class="head-sub">It will have to pair again before it can connect.</div></div>
        <div class="head-actions"><button class="btn plain" data-act="keep">Keep</button>
        <button class="btn red" data-act="forget-yes" data-name="${name}">Forget</button></div></li>`;
    }
    const sub = live.has(p.name) ? "Connected now" : `Paired ${esc(shortDate(p.added))}`;
    return `<li class="head${live.has(p.name) ? " live" : ""}"><div class="head-icon">${icon("headset")}</div>
      <div class="head-main"><div class="head-name">${name}</div>
      <div class="head-sub${p.always ? " always" : ""}">${sub}${p.always ? ". Takes control without asking." : ""}</div></div>
      <div class="head-actions">
        ${p.always ? `<button class="btn plain" data-act="ask" data-name="${name}" title="Ask on this laptop before it takes control">Ask first</button>` : ""}
        <button class="btn forget" data-act="forget" data-name="${name}">Forget</button></div></li>`;
  }).join("") : '<li class="empty">No headsets yet. Pair one above.</li>';
  const err = $("pairs-error");
  err.hidden = !s.pairs_error;
  err.textContent = s.pairs_error;
}

function renderSheet(s) {
  const addr = $("address");
  if (document.activeElement !== addr && addr.value !== s.settings.headset_address) addr.value = s.settings.headset_address;
  const w = $("wired-text");
  w.hidden = !(s.settings.wired && s.wired_text);
  if (w.textContent !== s.wired_text) w.textContent = s.wired_text;
  setText("kill", s.kill_switch);
  setText("laptop", s.laptop);
  setText("version", s.version || "");
  $("perm-group").hidden = !s.permissions;
  if (s.permissions) {
    const html = s.permissions.map(r => `<div class="perm"><span>${esc(r.name)}</span><small>${esc(r.state)}</small>
      ${r.name === "Input Monitoring" ? `<button class="btn plain" data-perm="${esc(r.name)}" data-request="1">Enable</button>` : ""}
      <button class="btn plain" data-perm="${esc(r.name)}">Open Settings</button></div>`).join("")
      + (s.permission_note ? `<p class="perm-note">${esc(s.permission_note)}</p>` : "");
    if ($("perms").dataset.html !== html) { $("perms").innerHTML = html; $("perms").dataset.html = html; }
  }
}

// ---------- wiring ----------

async function poll() {
  try {
    render(await api.poll(logSeq));
  } catch (e) {
    console.error(e);
  }
  setTimeout(poll, 250);
}

async function setMode(mode) {
  if (!snap || modeOf(snap.settings) === mode) return;
  let error = "";
  if (mode === "control") error = await api.set_toggle("allow_control", true);
  else if (mode === "cast") error = await api.set_toggle("cast_only", true);
  else error = await api.set_toggle(snap.settings.cast_only ? "cast_only" : "allow_control", false);
  toast(error);
}

async function startPairing(withCode) {
  typing = withCode;
  renderPairing(await api.pair_start());
  if (withCode) setTimeout(() => $("code").focus(), 0);
}

function openSheet(open) {
  $("sheet").hidden = !open;
  if (open) $("sheet-close").focus();
}

function wire() {
  paintIcons(document);
  document.querySelectorAll("[data-mode]").forEach(b => b.addEventListener("click", () => setMode(b.dataset.mode)));
  document.querySelectorAll("[data-setting]").forEach(box => box.addEventListener("change", async () => {
    const error = await api.set_toggle(box.dataset.setting, box.checked);
    toast(error);
  }));

  $("power").addEventListener("click", async () => {
    const on = snap && (snap.status.state === "running" || snap.status.state === "starting");
    await (on ? api.stop_all() : api.start());
  });

  $("qr-open").addEventListener("click", () => startPairing(false));
  $("type-open").addEventListener("click", () => startPairing(true));
  $("pair-switch").addEventListener("click", () => {
    typing = !typing;
    if (snap) renderPairing(snap.pair);
    if (typing) $("code").focus();
  });
  $("pair-cancel").addEventListener("click", async () => { await api.pair_cancel(); typing = false; renderPairing(null); });
  $("code-form").addEventListener("submit", async e => {
    e.preventDefault();
    const text = $("code").value.trim();
    if (!text) { $("code").focus(); return; }
    $("code").value = "";
    renderPairing(await api.pair_submit(text));
  });

  $("heads").addEventListener("click", async e => {
    const b = e.target.closest("[data-act]");
    if (!b) return;
    const name = b.dataset.name;
    if (b.dataset.act === "forget") {
      confirming = name;
    } else if (b.dataset.act === "keep") {
      confirming = "";
    } else if (b.dataset.act === "forget-yes") {
      confirming = "";
      const error = await api.forget(name);
      toast(error || `Forgot ${name}.`);
    } else if (b.dataset.act === "ask") {
      toast((await api.stop_always(name)) || `${name} will ask before it takes control.`);
    }
    lastHeadsKey = "";
    if (snap) renderHeads(snap);
  });

  $("more-open").addEventListener("click", () => openSheet(true));
  $("sheet-close").addEventListener("click", () => openSheet(false));
  $("sheet").addEventListener("click", e => { if (e.target === $("sheet")) openSheet(false); });
  document.addEventListener("keydown", e => {
    if (e.key !== "Escape") return;
    if (!$("sheet").hidden) openSheet(false);
    else if (confirming) { confirming = ""; lastHeadsKey = ""; if (snap) renderHeads(snap); }
  });
  $("addr-form").addEventListener("submit", async e => {
    e.preventDefault();
    const error = await api.set_address($("address").value);
    toast(error || "Saved.");
    $("address").blur();
  });
  document.addEventListener("click", e => {
    const perm = e.target.closest("[data-perm]");
    if (perm) api.open_permission(perm.dataset.perm, !!perm.dataset.request);
  });
  $("open-logs").addEventListener("click", () => api.open_log_folder());
  $("relaunch").addEventListener("click", () => api.relaunch());
  $("quit").addEventListener("click", () => {
    const now = Date.now();
    if (now - quitArmed < 4000) { api.quit(); return; }
    quitArmed = now;
    setText("quit", "Click again to quit. Every headset disconnects.");
    setTimeout(() => setText("quit", "Quit Spatial Launcher Agent"), 4000);
  });
  poll();
}

window.addEventListener("pywebviewready", () => { api = window.pywebview.api; wire(); });
