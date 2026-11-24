// Stark Studio: веб-редактор шагов сборки.
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';

const $ = (id) => document.getElementById(id);
const api = async (path, opts = {}) => {
  const r = await fetch(path, opts);
  const ct = r.headers.get('content-type') || '';
  const body = ct.includes('json') ? await r.json() : await r.blob();
  if (!r.ok) throw Object.assign(new Error(body.detail || body.errors?.join('\n') || r.statusText), { body, status: r.status });
  return body;
};

const state = { project: null, steps: [], hints: { ru: {}, kk: {}, en: {} }, sel: 0, dirty: false, playing: null };
const names = () => Object.fromEntries(state.project.parts.map((p) => [p.id, p.name]));
const baseName = (n) => n.replace(/[:#]\d+$/, '').trim();

// ---------- 3D ----------
const viewerEl = $('viewer');
const renderer = new THREE.WebGLRenderer({ antialias: true, preserveDrawingBuffer: true });
renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
renderer.setClearColor(0xeef0f3);
viewerEl.appendChild(renderer.domElement);
const scene = new THREE.Scene();
const camera = new THREE.PerspectiveCamera(40, 1, 0.001, 100);
const controls = new OrbitControls(camera, renderer.domElement);
controls.enableDamping = true;
scene.add(new THREE.HemisphereLight(0xffffff, 0x8890a0, 2.2));
const sun = new THREE.DirectionalLight(0xffffff, 1.6); sun.position.set(1, 2, 1.5); scene.add(sun);
const grid = new THREE.GridHelper(1, 20, 0xc5cad3, 0xdde1e7); scene.add(grid);

let modelRoot = null;
let modelSize = 1;
const partObj = {};   // partId -> Object3D
const partBase = {};  // partId -> {pos, mats}
const COL_CURRENT = new THREE.Color(0x22c3ee), COL_FAST = new THREE.Color(0xff8a1f);

function resize() {
  const w = viewerEl.clientWidth, h = viewerEl.clientHeight;
  renderer.setSize(w, h, false); camera.aspect = w / Math.max(h, 1); camera.updateProjectionMatrix();
}
new ResizeObserver(resize).observe(viewerEl);

async function loadModel(pid) {
  if (modelRoot) { scene.remove(modelRoot); }
  for (const k in partObj) delete partObj[k];
  const gltf = await new GLTFLoader().loadAsync(`/api/projects/${pid}/model.glb?t=${Date.now()}`);
  modelRoot = gltf.scene; scene.add(modelRoot);
  for (const p of state.project.parts) {
    const o = modelRoot.getObjectByName(p.id);
    if (!o) continue;
    partObj[p.id] = o;
    const mats = [];
    o.traverse((m) => { if (m.isMesh) { m.material = m.material.clone(); m.material.userData.base = m.material.color.clone(); m.userData.partId = p.id; mats.push(m.material); } });
    partBase[p.id] = { pos: o.position.clone(), mats };
  }
  const box = new THREE.Box3().setFromObject(modelRoot), size = box.getSize(new THREE.Vector3()), c = box.getCenter(new THREE.Vector3());
  const r = Math.max(size.x, size.y, size.z);
  modelSize = r;
  grid.scale.setScalar(r * 3); grid.position.y = box.min.y;
  controls.target.copy(c); camera.position.copy(c).add(new THREE.Vector3(r * 1.3, r * 1.0, r * 1.6));
  camera.near = r / 100; camera.far = r * 50; camera.updateProjectionMatrix();
}

function pathOf(step, pid) {
  const pp = step.partPaths?.[pid];
  if (pp) return pp;
  for (const f of step.fasteners || []) if (f.partPaths?.[pid]) return f.partPaths[pid];
  return null;
}

function stepOfPart(pid) {
  return state.steps.findIndex((s) => s.parts.includes(pid) || (s.fasteners || []).some((f) => f.partIds.includes(pid)));
}

const tmpV = new THREE.Vector3();
function localOffset(obj, arr, k) {
  // путь задан в координатах сцены glTF; переводим в систему родителя узла
  tmpV.set(arr[0], arr[1], arr[2]).multiplyScalar(k);
  if (obj.parent && obj.parent !== modelRoot.parent) {
    const m = new THREE.Matrix4().copy(obj.parent.matrixWorld).invert();
    tmpV.applyMatrix3(new THREE.Matrix3().setFromMatrix4(m));
  }
  return tmpV;
}

function animate(t) {
  requestAnimationFrame(animate);
  controls.update();
  if (modelRoot && state.steps.length) {
    const explode = +$('explode').value, hideFuture = $('hideFuture').checked;
    const phase = ((t / 2000) % 1), ease = phase < 0.75 ? 1 - Math.pow(1 - phase / 0.75, 3) : 1; // прилёт за 1.5 с, пауза 0.5 с
    const pulse = 0.5 + 0.5 * Math.sin(t / 180);
    for (const pid in partObj) {
      const o = partObj[pid], b = partBase[pid], si = stepOfPart(pid), st = state.steps[si];
      const isFast = st && (st.fasteners || []).some((f) => f.partIds.includes(pid));
      const status = si < state.sel ? 'done' : si === state.sel ? 'current' : 'future';
      o.position.copy(b.pos);
      const path = st ? pathOf(st, pid) : null;
      if (path && explode > 0 && si > 0) {
        // разлёт по направлениям установки: чем позже шаг, тем дальше деталь
        const len = Math.hypot(...path[0]) || 1;
        o.position.add(localOffset(o, path[0], explode * modelSize * 1.2 * (si / state.steps.length) / len));
      }
      if (status === 'current' && path) o.position.add(localOffset(o, path[0], 1 - ease));
      o.visible = !(status === 'future' && hideFuture);
      for (const m of b.mats) {
        if (status === 'current') {
          m.color.copy(isFast ? COL_FAST : COL_CURRENT); m.emissive?.copy(isFast ? COL_FAST : COL_CURRENT).multiplyScalar(0.25 + 0.35 * pulse);
          m.transparent = false; m.opacity = 1; m.depthWrite = true;
        } else if (status === 'done') {
          m.color.copy(m.userData.base); m.emissive?.setRGB(0, 0, 0); m.transparent = false; m.opacity = 1; m.depthWrite = true;
        } else {
          m.color.copy(m.userData.base); m.emissive?.setRGB(0, 0, 0); m.transparent = true; m.opacity = 0.13; m.depthWrite = false;
        }
      }
    }
  }
  renderer.render(scene, camera);
}
requestAnimationFrame(animate);

// клик по детали → выбрать её шаг
const ray = new THREE.Raycaster(); let downAt = null;
renderer.domElement.addEventListener('pointerdown', (e) => { downAt = [e.clientX, e.clientY]; });
renderer.domElement.addEventListener('pointerup', (e) => {
  if (!modelRoot || !downAt || Math.hypot(e.clientX - downAt[0], e.clientY - downAt[1]) > 4) return;
  const r = renderer.domElement.getBoundingClientRect();
  ray.setFromCamera(new THREE.Vector2(((e.clientX - r.left) / r.width) * 2 - 1, -((e.clientY - r.top) / r.height) * 2 + 1), camera);
  const hit = ray.intersectObject(modelRoot, true).find((h) => h.object.visible && h.object.material.opacity > 0.5);
  if (hit) { const si = stepOfPart(hit.object.userData.partId); if (si >= 0) select(si); }
});

// ---------- шаги ----------
function hintFor(step, lang = 'ru') { return state.hints[lang]?.[step.hintKey] || ''; }

function renderSteps() {
  const ol = $('stepList'); ol.innerHTML = '';
  $('stepCount').textContent = state.steps.length ? `(${state.steps.length})` : '';
  state.steps.forEach((s, i) => {
    const li = document.createElement('li');
    li.draggable = true; li.dataset.i = i;
    if (i === state.sel) li.className = 'sel';
    const fastCount = (s.fasteners || []).reduce((a, f) => a + f.qty, 0);
    li.innerHTML = `<div class="num">${String(i + 1).padStart(2, '0')}</div><div class="ttl"></div><div class="badges"></div>`;
    li.querySelector('.ttl').textContent = s.title;
    const bd = li.querySelector('.badges');
    const add = (cls, txt) => { const b = document.createElement('span'); b.className = 'badge ' + cls; b.textContent = txt; bd.appendChild(b); };
    if (s.parts.length > 1) add('', `${s.parts.length} дет.`);
    if (fastCount) add('fast', `крепёж ×${fastCount}`);
    if (s.review) add('warn', 'проверить');
    if (!hintFor(s)) add('err', 'нет подсказки');
    li.onclick = () => select(i);
    li.ondragstart = (e) => { e.dataTransfer.setData('text/plain', i); };
    li.ondragover = (e) => { e.preventDefault(); li.classList.add('dragover'); };
    li.ondragleave = () => li.classList.remove('dragover');
    li.ondrop = (e) => { e.preventDefault(); li.classList.remove('dragover'); moveStep(+e.dataTransfer.getData('text/plain'), i); };
    ol.appendChild(li);
  });
  updateLabel();
}

function updateLabel() {
  const s = state.steps[state.sel];
  $('stepLabel').textContent = s ? `Шаг ${state.sel + 1}/${state.steps.length}: ${hintFor(s) || s.title}` : '';
}

function select(i) {
  state.sel = Math.max(0, Math.min(i, state.steps.length - 1));
  renderSteps(); renderDetail();
  document.querySelector('#stepList li.sel')?.scrollIntoView({ block: 'nearest' });
}

function markDirty() { state.dirty = true; $('saveBtn').disabled = false; setStatus('Есть несохранённые изменения'); }

function relabel() {
  const n = names();
  state.steps.forEach((s, i) => {
    s.id = `S${String(i + 1).padStart(2, '0')}`;
    if (s.autoTitle !== false) {
      let t = `Установить: ${baseName(n[s.parts[0]] || s.parts[0])}` + (s.parts.length > 1 ? ` ×${s.parts.length}` : '');
      for (const f of s.fasteners || []) t += `, закрепить ${f.name} ×${f.qty}`;
      s.title = t;
    }
  });
}

function newHintKey() {
  const used = new Set(state.steps.map((s) => s.hintKey));
  let n = 1; while (used.has(`H${String(n).padStart(3, '0')}`)) n++;
  return `H${String(n).padStart(3, '0')}`;
}

function moveStep(from, to) {
  if (from === to || isNaN(from)) return;
  const [s] = state.steps.splice(from, 1); state.steps.splice(to, 0, s);
  relabel(); markDirty(); select(to);
}

function mergeNext(i) {
  const a = state.steps[i], b = state.steps[i + 1]; if (!b) return;
  a.parts.push(...b.parts); a.fasteners = [...(a.fasteners || []), ...(b.fasteners || [])];
  a.partPaths = { ...(a.partPaths || {}), ...(b.partPaths || {}) };
  a.checks = [...(a.checks || []), ...(b.checks || [])];
  a.review = a.review || b.review;
  state.steps.splice(i + 1, 1); relabel(); markDirty(); select(i);
}

function splitSelected(i, pids) {
  const a = state.steps[i];
  if (!pids.length || pids.length === a.parts.length) { setStatus('Отметьте часть деталей шага (не все)', 'err'); return; }
  a.parts = a.parts.filter((p) => !pids.includes(p));
  const paths = Object.fromEntries(pids.map((p) => [p, a.partPaths?.[p] || null]));
  const s = { parts: pids, fasteners: [], insertPath: paths[pids[0]], partPaths: paths, tool: '', checks: [], camera: { focus: pids[0], explode: 0 }, review: null, title: '', autoTitle: true, hintKey: newHintKey() };
  state.steps.splice(i + 1, 0, s); relabel(); markDirty(); select(i + 1);
}

function deleteStep(i) {
  // детали шага возвращаются в предыдущий (или следующий) шаг, чтобы ничего не потерять
  if (state.steps.length < 2) return;
  const j = i > 0 ? i - 1 : 1; const s = state.steps[i], t = state.steps[j];
  t.parts.push(...s.parts); t.fasteners.push(...(s.fasteners || [])); t.partPaths = { ...t.partPaths, ...s.partPaths };
  state.steps.splice(i, 1); relabel(); markDirty(); select(Math.min(j, state.steps.length - 1));
}

function renderDetail() {
  const d = $('detail'); const s = state.steps[state.sel]; d.innerHTML = '';
  $('detailId').textContent = s ? s.id : '';
  if (!s) return;
  const n = names(); const i = state.sel;
  const el = (html) => { const t = document.createElement('div'); t.innerHTML = html; return t.firstElementChild; };
  if (s.review) {
    const r = el(`<div class="review">⚠ Требует проверки: <span></span> <button class="ghost">Проверено</button></div>`);
    r.querySelector('span').textContent = s.review;
    r.querySelector('button').style.cssText = 'color:inherit;border-color:currentColor;padding:1px 8px;margin-left:6px';
    r.querySelector('button').onclick = () => { s.review = null; markDirty(); renderSteps(); renderDetail(); };
    d.appendChild(r);
  }
  const field = (label, input, extra) => { const f = el(`<div class="field"><label class="l"></label></div>`); f.querySelector('label').textContent = label; f.appendChild(input); if (extra) f.appendChild(extra); d.appendChild(f); return input; };

  const title = field('Название шага', el('<input type="text">'));
  title.value = s.title; title.oninput = () => { s.title = title.value; s.autoTitle = false; markDirty(); renderSteps(); };

  for (const lang of ['ru', 'kk', 'en']) {
    const ta = el('<textarea rows="2"></textarea>'); const cnt = el('<div class="counter"></div>');
    const upd = () => { const L = ta.value.length; cnt.textContent = `${L}/80`; cnt.classList.toggle('over', L > 80); };
    field(`Подсказка на очках (${lang})`, ta, cnt);
    ta.value = state.hints[lang]?.[s.hintKey] || ''; upd();
    ta.oninput = () => { (state.hints[lang] ||= {})[s.hintKey] = ta.value; upd(); markDirty(); if (lang === 'ru') { renderStepsKeepFocus(); } };
  }

  const tool = field('Инструмент', el('<input type="text" placeholder="например, шестигранник 5 мм">'));
  tool.value = s.tool || ''; tool.oninput = () => { s.tool = tool.value; markDirty(); };

  const checks = field('Проверки (по одной в строке)', el('<textarea rows="2" placeholder="Прокладка уложена в паз"></textarea>'));
  checks.value = (s.checks || []).join('\n'); checks.oninput = () => { s.checks = checks.value.split('\n').map((x) => x.trim()).filter(Boolean); markDirty(); };

  if ((s.fasteners || []).length) {
    const box = el('<div class="field"><label class="l">Крепёж</label></div>');
    s.fasteners.forEach((f, fi) => {
      const row = el(`<div class="fastener"><b></b> ×${f.qty}<br>момент, Н·м: <input type="number" step="0.1" min="0"> <button class="ghost" title="Сделать отдельным шагом">в отдельный шаг</button></div>`);
      row.querySelector('b').textContent = f.name;
      const inp = row.querySelector('input'); inp.value = f.torqueNm ?? '';
      inp.oninput = () => { f.torqueNm = inp.value === '' ? null : +inp.value; markDirty(); };
      const btn = row.querySelector('button'); btn.style.cssText = 'color:var(--text);border-color:var(--line);padding:1px 6px;margin-left:4px';
      btn.onclick = () => {
        s.fasteners.splice(fi, 1);
        state.steps.splice(i + 1, 0, { parts: f.partIds, fasteners: [], insertPath: f.partPaths?.[f.partIds[0]] || null, partPaths: f.partPaths || {}, tool: '', checks: [], camera: { focus: f.partIds[0], explode: 0 }, review: null, title: '', autoTitle: true, hintKey: newHintKey() });
        relabel(); markDirty(); select(i + 1);
      };
      box.appendChild(row);
    });
    d.appendChild(box);
  }

  const parts = el('<div class="field parts"><label class="l">Детали шага</label></div>');
  for (const pid of s.parts) {
    const lb = el('<label><input type="checkbox"> <span></span></label>');
    lb.querySelector('input').value = pid; lb.querySelector('span').textContent = `${n[pid] || pid}  (${pid})`;
    parts.appendChild(lb);
  }
  d.appendChild(parts);

  const row = el('<div class="row"></div>');
  const btn = (txt, fn, dis) => { const b = document.createElement('button'); b.textContent = txt; b.onclick = fn; b.disabled = !!dis; row.appendChild(b); };
  btn('Отмеченные → новый шаг', () => splitSelected(i, [...parts.querySelectorAll('input:checked')].map((x) => x.value)), s.parts.length < 2);
  btn('Объединить со следующим', () => mergeNext(i), i >= state.steps.length - 1);
  btn('↑', () => moveStep(i, i - 1), i === 0);
  btn('↓', () => moveStep(i, i + 1), i >= state.steps.length - 1);
  btn('Удалить шаг', () => deleteStep(i), state.steps.length < 2);
  d.appendChild(row);
}

function renderStepsKeepFocus() {
  const a = document.activeElement, pos = a?.selectionStart; renderSteps();
  if (a && document.body.contains(a)) { a.focus(); if (pos != null) a.setSelectionRange(pos, pos); }
}

// ---------- проект ----------
function setStatus(msg, cls = '') { const s = $('status'); s.textContent = msg; s.className = cls; }
function busy(on, text = 'Обработка…') { $('busy').hidden = !on; $('busyText').textContent = text; }

function showStats() {
  const st = state.project?.stats; if (!st) { $('stats').textContent = ''; return; }
  $('stats').textContent = `${state.project.name} · деталей ${st.parts} · шагов ${state.steps.length} · треугольников ${st.triangles.toLocaleString('ru')} · расчёт ${st.timings?.totalSec ?? '?'} с` +
    (st.interferences?.length ? ` · пересечений в CAD: ${st.interferences.length}` : '');
}

async function openProject(data, keepSel = false) {
  state.project = data; state.steps = structuredClone(data.steps); state.hints = structuredClone(data.hints);
  state.dirty = false; $('saveBtn').disabled = true;
  for (const id of ['hintsBtn', 'replanBtn', 'exportBtn']) $(id).disabled = false;
  $('emptyState').hidden = true;
  if (!keepSel) state.sel = 0;
  await loadModel(data.id);
  renderSteps(); renderDetail(); showStats();
  localStorageSet('stark.lastProject', data.id);
  $('projectSelect').value = data.id;
}

function localStorageSet(k, v) { try { localStorage.setItem(k, v); } catch { /* приватный режим */ } }
function localStorageGet(k) { try { return localStorage.getItem(k); } catch { return null; } }

async function refreshList(selectId) {
  const list = await api('/api/projects'); const sel = $('projectSelect');
  sel.innerHTML = '<option value="">— проект —</option>';
  for (const p of list) { const o = document.createElement('option'); o.value = p.id; o.textContent = `${p.name} (${p.parts} дет.)`; sel.appendChild(o); }
  if (selectId) sel.value = selectId;
  return list;
}

async function save() {
  if (!state.project) return;
  const data = await api(`/api/projects/${state.project.id}/steps`, { method: 'PUT', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ steps: state.steps, hints: state.hints }) });
  state.project = data; state.steps = structuredClone(data.steps); state.hints = structuredClone(data.hints);
  state.dirty = false; $('saveBtn').disabled = true; renderSteps(); renderDetail(); showStats(); setStatus('Сохранено', 'ok');
}

async function guarded(fn, text) {
  try { busy(true, text); await fn(); } catch (e) { console.error(e); setStatus(e.message, 'err'); } finally { busy(false); }
}

$('fileInput').onchange = (e) => guarded(async () => {
  const f = e.target.files[0]; if (!f) return;
  const fd = new FormData(); fd.append('file', f); fd.append('up_axis', $('upAxis').value);
  const data = await api('/api/projects', { method: 'POST', body: fd });
  await refreshList(data.id); await openProject(data); setStatus(`Импортировано: ${data.stats.parts} деталей, ${data.stats.steps} шагов`, 'ok');
  e.target.value = '';
}, 'Импорт STEP и расчёт шагов…');

const openSample = () => guarded(async () => {
  const data = await api('/api/samples/gearbox', { method: 'POST' });
  await refreshList(data.id); await openProject(data); setStatus('Открыта тестовая сборка', 'ok');
}, 'Создаю тестовую сборку…');
$('sampleBtn').onclick = openSample; $('sampleBtn2').onclick = openSample;

$('projectSelect').onchange = (e) => e.target.value && guarded(async () => openProject(await api(`/api/projects/${e.target.value}`)), 'Загрузка…');
$('saveBtn').onclick = () => guarded(save, 'Сохранение…');

$('hintsBtn').onclick = () => guarded(async () => {
  if (state.dirty) await save();
  const lang = $('hintLang').value;
  const r = await api(`/api/projects/${state.project.id}/hints`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ lang }) });
  await openProject(r.project, true); setStatus(`Подсказки (${lang}): ${r.source === 'llm' ? 'сгенерированы LLM' : r.source}`, r.source === 'llm' ? 'ok' : '');
}, 'Пишу подсказки…');

$('replanBtn').onclick = () => {
  if (!confirm('Пересчитать шаги из CAD? Ручные правки шагов и подсказок будут потеряны.')) return;
  guarded(async () => { await openProject(await api(`/api/projects/${state.project.id}/replan`, { method: 'POST' })); setStatus('Шаги пересчитаны', 'ok'); }, 'Расчёт шагов…');
};

$('exportBtn').onclick = () => guarded(async () => {
  if (state.dirty) await save();
  const v = await api(`/api/projects/${state.project.id}/validate`);
  if (v.errors.length) { setStatus('Экспорт невозможен: ' + v.errors.join('; '), 'err'); return; }
  const blob = await api(`/api/projects/${state.project.id}/export`);
  const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = `${state.project.id}.stark`; a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 5000);
  setStatus(`Пакет .stark сохранён` + (v.warnings.length ? `. Предупреждения: ${v.warnings.join('; ')}` : ''), 'ok');
}, 'Упаковка .stark…');

$('prevBtn').onclick = () => select(state.sel - 1);
$('nextBtn').onclick = () => select(state.sel + 1);
$('playBtn').onclick = () => {
  if (state.playing) { clearInterval(state.playing); state.playing = null; $('playBtn').textContent = '▶ Проиграть'; return; }
  select(0); $('playBtn').textContent = '■ Стоп';
  state.playing = setInterval(() => { if (state.sel >= state.steps.length - 1) { $('playBtn').click(); } else select(state.sel + 1); }, 2600);
};
document.addEventListener('keydown', (e) => {
  if (['INPUT', 'TEXTAREA', 'SELECT'].includes(document.activeElement?.tagName)) return;
  if (e.key === 'ArrowRight' || e.key === 'ArrowDown') { e.preventDefault(); select(state.sel + 1); }
  if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') { e.preventDefault(); select(state.sel - 1); }
});
window.addEventListener('beforeunload', (e) => { if (state.dirty) { e.preventDefault(); e.returnValue = ''; } });

(async () => {
  try {
    const list = await refreshList();
    const last = localStorageGet('stark.lastProject');
    const pick = list.find((p) => p.id === last) || null;
    if (pick) await guarded(async () => openProject(await api(`/api/projects/${pick.id}`)), 'Загрузка…');
  } catch (e) { setStatus('Сервер недоступен: ' + e.message, 'err'); }
})();
