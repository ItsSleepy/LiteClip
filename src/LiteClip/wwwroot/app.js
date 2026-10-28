'use strict';

// ---------------------------------------------------------------- bridge
const $ = (s, el = document) => el.querySelector(s);
const $$ = (s, el = document) => [...el.querySelectorAll(s)];
const host = window.chrome && window.chrome.webview;
let reqId = 0;
const pending = new Map();
const handlers = {};

function rpc(cmd, args = {}) {
  return new Promise((resolve, reject) => {
    if (!host) return reject(new Error('not running inside LiteClip'));
    const id = ++reqId;
    pending.set(id, { resolve, reject });
    host.postMessage({ id, cmd, args });
  });
}
host && host.addEventListener('message', e => {
  const m = e.data;
  if (m && m.id && pending.has(m.id)) {
    const p = pending.get(m.id); pending.delete(m.id);
    m.ok ? p.resolve(m.result) : p.reject(new Error(m.error || 'failed'));
  } else if (m && m.event && handlers[m.event]) handlers[m.event](m.data);
});

// ---------------------------------------------------------------- state
const S = {
  settings: null, clips: [], status: null, encoders: [], mics: [], outputs: [], monitors: [],
  filter: { q: '', game: '', fav: false }, newest: null, view: 'clips',
};

// ---------------------------------------------------------------- helpers
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const fmtLen = s => { s = Math.max(0, s || 0); const m = Math.floor(s / 60), r = Math.floor(s % 60); return `${m}:${String(r).padStart(2, '0')}`; };
const fmtLenTenths = s => { s = Math.max(0, s || 0); const m = Math.floor(s / 60), r = (s % 60).toFixed(1); return `${m}:${r.padStart(4, '0')}`; };
const fmtSize = b => !b ? '0 MB' : b >= 1e9 ? (b / 1e9).toFixed(1) + ' GB' : b >= 1e6 ? (b / 1e6).toFixed(b >= 1e8 ? 0 : 1) + ' MB' : Math.max(1, Math.round(b / 1e3)) + ' KB';
const lenLabel = s => s >= 60 ? `${s / 60} minute${s === 60 ? '' : 's'}` : `${s} seconds`;
const dayKey = d => `${d.getFullYear()}-${d.getMonth()}-${d.getDate()}`;
function dayLabel(d) {
  const now = new Date(); const y = new Date(now); y.setDate(now.getDate() - 1);
  if (dayKey(d) === dayKey(now)) return 'Today';
  if (dayKey(d) === dayKey(y)) return 'Yesterday';
  return d.toLocaleDateString(undefined, { weekday: 'short', day: 'numeric', month: 'short', year: d.getFullYear() === now.getFullYear() ? undefined : 'numeric' });
}
const timeLabel = d => d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });

function toast(text, kind = '', ms = 3200) {
  const el = document.createElement('div');
  el.className = 'toast ' + kind;
  el.textContent = text;
  $('#toasts').append(el);
  const kill = () => { el.style.transition = 'opacity .25s'; el.style.opacity = 0; setTimeout(() => el.remove(), 260); };
  if (ms) setTimeout(kill, ms);
  return { el, done: (t, k = '') => { el.textContent = t; el.className = 'toast ' + k; setTimeout(kill, 3200); } };
}

function confirmBox(title, text, yes = 'Delete') {
  return new Promise(res => {
    $('#m-title').textContent = title; $('#m-text').textContent = text; $('#m-yes').textContent = yes;
    $('#modal').hidden = false;
    const done = v => { $('#modal').hidden = true; $('#m-yes').onclick = $('#m-no').onclick = null; res(v); };
    $('#m-yes').onclick = () => done(true); $('#m-no').onclick = () => done(false);
    $('#m-yes').focus();
  });
}

// ---------------------------------------------------------------- navigation
function show(view) {
  S.view = view;
  $$('.nav').forEach(b => b.classList.toggle('on', b.dataset.view === view));
  $$('.view').forEach(v => v.classList.toggle('on', v.id === 'view-' + view));
  rpc('meters', { on: view === 'settings' }).catch(() => {});
  if (view === 'settings') loadStorage();
}
$$('.nav').forEach(b => b.onclick = () => show(b.dataset.view));

// ---------------------------------------------------------------- status
let statusTimer;
function renderStatus() {
  const st = S.status; if (!st) return;
  const box = $('#status');
  box.className = 'status ' + st.state;
  $('#st-text').textContent = st.text || '';
  const bits = [];
  if (st.state === 'recording') {
    if (st.height) bits.push(`${st.height}p${st.fps}`);
    if (st.encoder) bits.push(st.encoder.replace(/ \(.+\)/, ''));
    if (st.since) bits.push(fmtLen((Date.now() - Date.parse(st.since)) / 1000));
  } else if (st.state === 'idle' && S.settings) {
    bits.push(S.settings.onlyWhileGaming ? 'Waiting for a game' : '');
  }
  $('#st-detail').textContent = bits.filter(Boolean).join(' · ');
  $('#pause-btn').textContent = st.paused ? 'Resume' : 'Pause';
}
handlers.status = st => { S.status = st; renderStatus(); };
statusTimer = setInterval(() => S.status && S.status.state === 'recording' && renderStatus(), 1000);
$('#pause-btn').onclick = async () => { S.status = await rpc('pause', { on: !(S.status && S.status.paused) }); renderStatus(); };

// ---------------------------------------------------------------- clips
const STAR = '<svg viewBox="0 0 24 24"><path d="m12 3.8 2.5 5.1 5.6.8-4 4 1 5.5-5.1-2.7-5 2.7 1-5.5-4.1-4 5.6-.8z"/></svg>';
const PLAY = '<svg viewBox="0 0 24 24"><path d="M8 5.5v13l10.5-6.5z" class="fill"/></svg>';

function filteredClips() {
  const { q, game, fav } = S.filter;
  const words = q.toLowerCase().split(/\s+/).filter(Boolean);
  return S.clips.filter(c =>
    (!game || c.game === game) && (!fav || c.favourite) &&
    words.every(w => (c.name + ' ' + c.game).toLowerCase().includes(w)));
}

function renderGames() {
  const counts = {};
  S.clips.forEach(c => counts[c.game] = (counts[c.game] || 0) + 1);
  const games = Object.keys(counts).sort((a, b) => counts[b] - counts[a]);
  if (S.filter.game && !counts[S.filter.game]) S.filter.game = '';
  const el = $('#games');
  el.hidden = games.length < 2;
  el.innerHTML = `<button data-g="" class="${S.filter.game ? '' : 'on'}">All<span>${S.clips.length}</span></button>` +
    games.map(g => `<button data-g="${esc(g)}" class="${S.filter.game === g ? 'on' : ''}">${esc(g)}<span>${counts[g]}</span></button>`).join('');
  $$('button', el).forEach(b => b.onclick = () => { S.filter.game = b.dataset.g; renderClips(); });
}

function renderClips() {
  renderGames();
  const list = filteredClips();
  const groups = new Map();
  for (const c of list) {
    const d = new Date(c.created);
    const k = dayKey(d);
    if (!groups.has(k)) groups.set(k, { label: dayLabel(d), items: [] });
    groups.get(k).items.push(c);
  }
  $('#empty').hidden = S.clips.length > 0;
  if (!S.clips.length) { $('#clip-list').innerHTML = ''; return; }
  if (!list.length) { $('#clip-list').innerHTML = '<p class="note" style="padding:30px 0">No clips match.</p>'; return; }
  $('#clip-list').innerHTML = [...groups.values()].map(g => `
    <div class="day"><h3>${esc(g.label)}</h3><div class="grid">${g.items.map(card).join('')}</div></div>`).join('');
  $$('.card').forEach(el => {
    const c = list.find(x => x.file === el.dataset.file);
    el.onclick = () => openPlayer(c);
    el.onkeydown = e => { if (e.key === 'Enter') openPlayer(c); };
    const img = $('img', el);
    img.onload = () => img.classList.add('ok');
    img.onerror = () => fetchThumb(c, img);
  });
}

function card(c) {
  const d = new Date(c.created);
  const isNew = S.newest && S.newest === c.file;
  return `<div class="card" tabindex="0" data-file="${esc(c.file)}">
    <div class="thumb">
      <img loading="lazy" src="${esc(c.thumb)}" alt="">
      ${c.duration ? `<span class="len">${fmtLen(c.duration)}</span>` : ''}
      ${isNew ? '<span class="new">NEW</span>' : c.favourite ? `<span class="star">${STAR}</span>` : ''}
      <div class="play"><span>${PLAY}</span></div>
    </div>
    <div class="meta"><b title="${esc(c.name)}">${esc(c.name)}</b><small>${esc(c.game)} · ${timeLabel(d)} · ${fmtSize(c.size)}</small></div>
  </div>`;
}

const thumbTried = new Set();
async function fetchThumb(c, img) {
  if (thumbTried.has(c.file)) return;
  thumbTried.add(c.file);
  try {
    const url = await rpc('thumb', { file: c.file });
    if (url) img.src = url + '?v=' + Date.now();
  } catch { }
}

async function reloadClips() {
  try { S.clips = await rpc('clips'); renderClips(); } catch (e) { console.error(e); }
}
handlers.clips = () => reloadClips();
handlers.clipSaved = item => {
  if (item) S.newest = item.file;
  reloadClips();
};

$('#q').oninput = e => { S.filter.q = e.target.value; renderClips(); };
$('#fav-filter').onclick = () => { S.filter.fav = !S.filter.fav; $('#fav-filter').classList.toggle('on', S.filter.fav); renderClips(); };
$('#save-btn').onclick = () => rpc('saveClip', { long: false }).catch(e => toast(e.message, 'bad'));

// ---------------------------------------------------------------- player
const P = { clip: null, dur: 0, inS: 0, outS: 0, drag: null };
const video = $('#video');

function openPlayer(c) {
  if (!c) return;
  P.clip = c; P.dur = c.duration || 0; P.inS = 0; P.outS = P.dur;
  $('#p-name').value = c.name;
  $('#p-fav').classList.toggle('on', !!c.favourite);
  $('#p-meta').textContent = `${c.game} · ${new Date(c.created).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })} · ${fmtSize(c.size)}`;
  $('#player').hidden = false;
  video.src = c.url + '?v=' + c.size;
  video.muted = localStorage.getItem('muted') === '1';
  $('#p-mute').classList.toggle('muted', video.muted);
  video.play().catch(() => {});
  if (S.newest === c.file) S.newest = null;
  layoutTimeline();
}

function closePlayer() {
  video.pause();
  video.removeAttribute('src');
  video.load();
  $('#player').hidden = true;
  P.clip = null;
}

function releaseVideo() { const t = video.currentTime; video.pause(); video.removeAttribute('src'); video.load(); return t; }

video.addEventListener('loadedmetadata', () => {
  if (!P.clip) return;
  const d = video.duration;
  if (isFinite(d) && d > 0) {
    const wasFull = P.outS === 0 || Math.abs(P.outS - P.dur) < 0.01;
    P.dur = d;
    if (wasFull) P.outS = d;
    const st = $('#p-meta');
    if (video.videoHeight && !st.textContent.includes('p ·')) st.textContent += ` · ${video.videoHeight}p`;
  }
  layoutTimeline();
});
video.addEventListener('timeupdate', () => {
  if (!P.clip) return;
  if (!video.paused && P.outS > 0 && video.currentTime >= P.outS - 0.02) { video.currentTime = P.inS; }
  layoutTimeline();
});
video.addEventListener('play', () => $('#p-play').classList.add('playing'));
video.addEventListener('pause', () => $('#p-play').classList.remove('playing'));

function togglePlay() {
  if (video.paused) {
    if (video.currentTime < P.inS || video.currentTime >= P.outS - 0.05) video.currentTime = P.inS;
    video.play().catch(() => {});
  } else video.pause();
}
$('#p-play').onclick = togglePlay;
video.onclick = togglePlay;
$('#p-mute').onclick = () => { video.muted = !video.muted; localStorage.setItem('muted', video.muted ? '1' : '0'); $('#p-mute').classList.toggle('muted', video.muted); };
$('#p-close').onclick = closePlayer;

function layoutTimeline() {
  const d = P.dur || 1;
  const pct = v => (Math.max(0, Math.min(d, v)) / d * 100) + '%';
  $('#tl-in').style.left = pct(P.inS);
  $('#tl-out').style.left = pct(P.outS);
  $('#tl-keep').style.left = pct(P.inS);
  $('#tl-keep').style.width = ((P.outS - P.inS) / d * 100) + '%';
  $('#tl-head').style.left = pct(video.currentTime || 0);
  $('#p-time').textContent = `${fmtLen(video.currentTime)} / ${fmtLen(P.dur)}`;
  const trimmed = P.inS > 0.05 || P.outS < P.dur - 0.05;
  $('#a-trim-copy').disabled = $('#a-trim-replace').disabled = !trimmed;
  $('#trim-info').innerHTML = trimmed
    ? `Keeping <b>${fmtLenTenths(P.inS)}</b> → <b>${fmtLenTenths(P.outS)}</b> (${(P.outS - P.inS).toFixed(1)}s)`
    : 'Drag the white handles to trim · <kbd>I</kbd> / <kbd>O</kbd> set start and end';
}

const tl = $('#timeline');
function timeAt(e) { const r = tl.getBoundingClientRect(); return Math.max(0, Math.min(1, (e.clientX - r.left) / r.width)) * P.dur; }
tl.addEventListener('pointerdown', e => {
  if (!P.dur) return;
  tl.setPointerCapture(e.pointerId);
  if (e.target.id === 'tl-in') P.drag = 'in';
  else if (e.target.id === 'tl-out') P.drag = 'out';
  else { P.drag = 'seek'; video.currentTime = timeAt(e); }
  layoutTimeline();
});
tl.addEventListener('pointermove', e => {
  if (!P.drag) return;
  const t = timeAt(e);
  if (P.drag === 'in') { P.inS = Math.min(t, P.outS - 0.5); video.currentTime = P.inS; }
  else if (P.drag === 'out') { P.outS = Math.max(t, P.inS + 0.5); video.currentTime = P.outS; }
  else video.currentTime = t;
  layoutTimeline();
});
tl.addEventListener('pointerup', () => { P.drag = null; });

document.addEventListener('keydown', e => {
  if ($('#player').hidden || listening || e.target.tagName === 'INPUT') return;
  const k = e.key.toLowerCase();
  if (k === ' ') { e.preventDefault(); togglePlay(); }
  else if (k === 'escape') closePlayer();
  else if (k === 'i') { P.inS = Math.min(video.currentTime, P.outS - 0.5); layoutTimeline(); }
  else if (k === 'o') { P.outS = Math.max(video.currentTime, P.inS + 0.5); layoutTimeline(); }
  else if (k === 'arrowleft') video.currentTime = Math.max(0, video.currentTime - (e.shiftKey ? 1 : 5));
  else if (k === 'arrowright') video.currentTime = Math.min(P.dur, video.currentTime + (e.shiftKey ? 1 : 5));
  else if (k === ',') { video.pause(); video.currentTime = Math.max(0, video.currentTime - 1 / 60); }
  else if (k === '.') { video.pause(); video.currentTime = Math.min(P.dur, video.currentTime + 1 / 60); }
  else if (k === 'm') $('#p-mute').click();
});

// rename: blur or Enter
$('#p-name').addEventListener('keydown', e => { if (e.key === 'Enter') e.target.blur(); if (e.key === 'Escape') { e.target.value = P.clip.name; e.target.blur(); } });
$('#p-name').addEventListener('blur', async e => {
  const name = e.target.value.trim();
  if (!P.clip || !name || name === P.clip.name) { e.target.value = P.clip ? P.clip.name : ''; return; }
  const t = releaseVideo();
  try {
    S.clips = await rpc('rename', { file: P.clip.file, name });
    const c = S.clips.find(x => x.name === name) || S.clips.find(x => x.name.startsWith(name));
    renderClips();
    if (c) { P.clip = c; video.src = c.url + '?v=' + c.size; video.currentTime = t; }
  } catch (err) {
    toast(err.message, 'bad'); e.target.value = P.clip.name; video.src = P.clip.url + '?v=' + P.clip.size;
  }
});

$('#p-fav').onclick = async () => {
  const on = !P.clip.favourite;
  S.clips = await rpc('favourite', { file: P.clip.file, on });
  P.clip.favourite = on;
  $('#p-fav').classList.toggle('on', on);
  renderClips();
};

$('#a-copy').onclick = async () => {
  try { await rpc('copy', { file: P.clip.file }); toast('Copied. Paste it into Discord (Ctrl+V)'); }
  catch (e) { toast(e.message, 'bad'); }
};

$('#a-discord').onclick = async () => {
  const t = toast('Making a Discord-size copy…', 'busy', 0);
  $('#a-discord').disabled = true;
  try {
    const r = await rpc('discord', { file: P.clip.file, copy: true });
    t.done(`Copied a ${fmtSize(r.size)} version. Paste it into Discord`);
  } catch (e) { t.done(e.message, 'bad'); }
  finally { $('#a-discord').disabled = false; }
};

$('#a-reveal').onclick = () => rpc('reveal', { file: P.clip.file });

$('#a-delete').onclick = async () => {
  if (!await confirmBox('Delete this clip?', `"${P.clip.name}" goes to the Recycle Bin.`)) return;
  const file = P.clip.file;
  releaseVideo();
  try { S.clips = await rpc('delete', { file }); closePlayer(); renderClips(); toast('Moved to the Recycle Bin'); }
  catch (e) { toast(e.message, 'bad'); video.src = P.clip.url; }
};

async function doTrim(replace) {
  if (replace && !await confirmBox('Trim the original?', 'The cut-off parts are gone for good. "Save trimmed copy" keeps the original instead.', 'Trim')) return;
  const t = toast('Trimming…', 'busy', 0);
  const file = P.clip.file;
  if (replace) releaseVideo();
  try {
    const r = await rpc('trim', { file, start: P.inS, end: P.outS, replace });
    S.clips = r.clips; renderClips();
    const c = S.clips.find(x => x.file === r.file);
    t.done(replace ? 'Trimmed' : 'Saved a trimmed copy');
    if (c) openPlayer(c);
  } catch (e) { t.done(e.message, 'bad'); if (replace) video.src = P.clip.url; }
}
$('#a-trim-copy').onclick = () => doTrim(false);
$('#a-trim-replace').onclick = () => doTrim(true);

// ---------------------------------------------------------------- settings
let saveTimer;
function queueSave() {
  clearTimeout(saveTimer);
  saveTimer = setTimeout(async () => {
    try {
      S.settings = await rpc('saveSettings', { settings: S.settings });
      fillSettings();
      const s = $('#saved'); s.classList.add('show'); setTimeout(() => s.classList.remove('show'), 1400);
    } catch (e) { toast(e.message, 'bad'); }
  }, 250);
}

function setValue(key, v) { S.settings[key] = v; fillSettings(); queueSave(); }

function fillSettings() {
  const s = S.settings; if (!s) return;
  $$('[data-set]').forEach(el => {
    const k = el.dataset.set, v = s[k];
    if (el.classList.contains('seg')) $$('button', el).forEach(b => b.classList.toggle('on', String(v) === b.value));
    else if (el.type === 'checkbox') el.checked = !!v;
    else if (el.type === 'range') { el.value = v; el.nextElementSibling.textContent = v + '%'; }
    else if (el.tagName === 'SELECT') el.value = String(v);
  });
  $$('.key').forEach(b => { if (!b.classList.contains('listening')) b.textContent = s[b.dataset.key]; });
  $('#folder-path').textContent = s.clipFolder;
  $('#save-key').textContent = s.clipKey;
  $$('.k-clip').forEach(e => e.textContent = s.clipKey);
  $$('.k-long').forEach(e => e.textContent = s.longClipKey);
  $$('.k-len').forEach(e => e.textContent = lenLabel(s.clipSeconds));
  $('#quality-hint').textContent = { low: 'Up to 720p, small files', medium: 'Up to 1080p', high: 'Up to 1440p, sharp', max: 'Full resolution up to 4K, big files' }[s.quality];
  $('#row-monitor').classList.toggle('dim', s.onlyWhileGaming && s.captureMode === 'game');
  $$('[data-set=gameDevice],[data-set=gameVolume]').forEach(e => e.closest('.row').classList.toggle('dim', !s.recordGameAudio));
  $$('[data-set=micDevice],[data-set=micVolume]').forEach(e => e.closest('.row').classList.toggle('dim', !s.recordMic));
  $$('.tags').forEach(renderTags);
}

function fillSelects() {
  const opt = (v, t) => `<option value="${esc(v)}">${esc(t)}</option>`;
  const def = list => (list.find(d => d.isDefault) || {}).name;
  $('#mics').innerHTML = opt('', `Windows default${def(S.mics) ? ` (${def(S.mics)})` : ''}`) + S.mics.map(d => opt(d.id, d.name)).join('');
  $('#outputs').innerHTML = opt('', `Windows default${def(S.outputs) ? ` (${def(S.outputs)})` : ''}`) + S.outputs.map(d => opt(d.id, d.name)).join('');
  $('#monitors').innerHTML = S.monitors.map(m => opt(m.index, `${m.name} · ${m.width}×${m.height}`)).join('');
  fillEncoders();
}

function fillEncoders() {
  const opt = (v, t) => `<option value="${esc(v)}">${esc(t)}</option>`;
  const best = S.encoders[0];
  $('#encoders').innerHTML = opt('auto', `Automatic${best ? ` (${best.name})` : ''}`) + S.encoders.map(e => opt(e.id, e.name)).join('');
  $('#encoders').value = S.encoders.some(e => e.id === S.settings.encoder) ? S.settings.encoder : 'auto';
  $('#encoder-hint').textContent = !S.encoders.length ? 'No working encoder found. Try updating your graphics driver.'
    : best && best.id === 'mf-sw' ? 'Only the CPU encoder works here, which costs FPS. Try updating your graphics driver.'
    : 'Your graphics card does the work, so games barely lose FPS';
}

$$('.seg').forEach(seg => $$('button', seg).forEach(b => b.onclick = async () => {
  const key = seg.dataset.set;
  const v = seg.dataset.type === 'int' ? parseInt(b.value, 10) : b.value;
  if (key === 'codec') {
    const list = await rpc('encoders', { codec: v });
    if (!list.length) { toast(`Your graphics card can't record ${b.textContent}.`, 'bad'); return; }
    S.encoders = list;
    S.settings.codec = v;
    fillEncoders();
  }
  setValue(key, v);
}));
$$('input[type=checkbox][data-set]').forEach(el => el.onchange = () => setValue(el.dataset.set, el.checked));
$$('select[data-set]').forEach(el => el.onchange = () => setValue(el.dataset.set, el.dataset.type === 'int' ? parseInt(el.value, 10) : el.value));
$$('input[type=range][data-set]').forEach(el => {
  el.oninput = () => { el.nextElementSibling.textContent = el.value + '%'; S.settings[el.dataset.set] = parseInt(el.value, 10); queueSave(); };
});

$('#pick-folder').onclick = async () => { const p = await rpc('pickFolder'); if (p) setValue('clipFolder', p); };
$('#open-folder').onclick = () => rpc('openFolder', { which: 'clips' });
$('#open-logs').onclick = () => rpc('openFolder', { which: 'logs' });
$('#quit').onclick = async () => { if (await confirmBox('Quit LiteClip?', 'Clipping stops until you open it again.', 'Quit')) rpc('quit'); };

// ---- key binding
let listening = null;
const KEYNAMES = { ' ': 'Space', Escape: 'Escape', ArrowUp: 'Up', ArrowDown: 'Down', ArrowLeft: 'Left', ArrowRight: 'Right',
  PageUp: 'PageUp', PageDown: 'PageDown', Insert: 'Insert', Delete: 'Delete', Home: 'Home', End: 'End', Pause: 'Pause',
  ScrollLock: 'Scroll', PrintScreen: 'PrintScreen', Backspace: 'Back', Enter: 'Enter', Tab: 'Tab', CapsLock: 'Capital' };
function keyName(e) {
  const c = e.code;
  if (/^F\d{1,2}$/.test(c)) return c;
  if (/^Key[A-Z]$/.test(c)) return c.slice(3);
  if (/^Digit\d$/.test(c)) return c.slice(5);
  if (/^Numpad\d$/.test(c)) return 'NumPad' + c.slice(6);
  if (c === 'Backquote') return '`';
  if (c === 'Minus') return '-';
  if (c === 'Equal') return '=';
  return KEYNAMES[e.key] || null;
}
$$('.key').forEach(b => b.onclick = () => {
  if (listening) return;
  listening = b;
  b.classList.add('listening');
  b.textContent = 'Press a key…';
  rpc('suspendHotkeys', { on: true });
});
window.addEventListener('keydown', e => {
  if (!listening) return;
  e.preventDefault(); e.stopPropagation();
  if (['Control', 'Shift', 'Alt', 'Meta'].includes(e.key)) return;
  const b = listening;
  const finish = () => { listening = null; b.classList.remove('listening'); rpc('suspendHotkeys', { on: false }); fillSettings(); };
  if (e.key === 'Escape') return finish();
  const name = keyName(e);
  if (!name) { toast("That key can't be used", 'bad'); return finish(); }
  const combo = [e.ctrlKey && 'Ctrl', e.shiftKey && 'Shift', e.altKey && 'Alt', e.metaKey && 'Win', name].filter(Boolean).join('+');
  const other = b.dataset.key === 'clipKey' ? 'longClipKey' : 'clipKey';
  if (combo === S.settings[other]) { toast('That key is already used for the other clip', 'bad'); return finish(); }
  S.settings[b.dataset.key] = combo;
  finish();
  queueSave();
}, true);

// ---- game lists
function renderTags(box) {
  const key = box.dataset.list;
  const items = S.settings[key] || [];
  box.innerHTML = items.map((x, i) => `<span>${esc(x)}<button data-i="${i}" title="Remove">×</button></span>`).join('') +
    `<input placeholder="add, e.g. game.exe" spellcheck="false">`;
  $$('button', box).forEach(b => b.onclick = () => { items.splice(+b.dataset.i, 1); S.settings[key] = items; renderTags(box); queueSave(); });
  const inp = $('input', box);
  inp.onkeydown = e => {
    if (e.key !== 'Enter') return;
    const v = inp.value.trim().toLowerCase();
    if (!v) return;
    if (!items.includes(v)) items.push(v);
    S.settings[key] = items;
    renderTags(box);
    $('input', box).focus();
    queueSave();
  };
}

// ---- meters
handlers.levels = l => {
  const w = p => Math.min(100, Math.sqrt(p || 0) * 100) + '%';
  $('#m-game').style.width = w(l.game);
  $('#m-mic').style.width = w(l.mic);
};

// ---- storage
async function loadStorage() {
  try {
    const s = await rpc('storage');
    const total = s.total || (s.clips + s.discord + s.buffer + s.thumbs + s.free) || 1;
    const parts = [
      ['Clips', s.clips, 'var(--accent)'],
      ['Discord copies', s.discord, 'var(--accent-2)'],
      ['Recording buffer', s.buffer, 'var(--amber)'],
      ['Thumbnails', s.thumbs, '#6b7085'],
    ];
    $('#storage').innerHTML =
      `<div class="bar">${parts.map(p => `<i style="width:${p[1] / total * 100}%;background:${p[2]}"></i>`).join('')}</div>
       <div class="legend">${parts.map(p => `<div><em style="background:${p[2]}"></em>${p[0]}<b>${fmtSize(p[1])}</b></div>`).join('')}
       <div><em style="background:var(--panel-3)"></em>Free on drive<b>${fmtSize(s.free)}</b></div></div>`;
  } catch { }
}

// ---------------------------------------------------------------- start
(async function init() {
  if (!host) { document.body.insertAdjacentHTML('afterbegin', '<p style="padding:20px">Open this inside LiteClip.</p>'); return; }
  try {
    const d = await rpc('init');
    Object.assign(S, { settings: d.settings, status: d.status, clips: d.clips, mics: d.mics, outputs: d.outputs, monitors: d.monitors, encoders: d.encoders });
    $('#version').textContent = d.version;
    fillSelects();
    fillSettings();
    renderStatus();
    renderClips();
  } catch (e) { toast('Startup problem: ' + e.message, 'bad', 0); }
})();
