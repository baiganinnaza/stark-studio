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
const baseName = (n) => n.replace(/([:#]\d+|_\d+)$/, '').trim();

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
const COL_CURRENT = new THREE.Color(0x22c3ee), COL_FAST = new THREE.Color(0xff8a1f), COL_INTER = new THREE.Color(0xe53935);
const partCenter = {}; // partId -> центр детали в собранном виде (координаты сцены)
const arrows = new THREE.Group(); scene.add(arrows);

function resize() {
  const w = viewerEl.clientWidth, h = viewerEl.clientHeight;
  renderer.setSize(w, h, false); camera.aspect = w / Math.max(h, 1); camera.updateProjectionMatrix();
}
new ResizeObserver(resize).observe(viewerEl);

async function loadModel(pid) {
  if (modelRoot) { scene.remove(modelRoot); }
  for (const k in partObj) delete partObj[k];
  for (const k in partCenter) delete partCenter[k];
  const gltf = await new GLTFLoader().loadAsync(`/api/projects/${pid}/model.glb?t=${Date.now()}`);
  modelRoot = gltf.scene; scene.add(modelRoot);
  for (const p of state.project.parts) {
    const o = modelRoot.getObjectByName(p.id);
    if (!o) continue;
    partObj[p.id] = o;
    const mats = [];
    o.traverse((m) => { if (m.isMesh) { m.material = m.material.clone(); m.material.userData.base = m.material.color.clone(); m.userData.partId = p.id; mats.push(m.material); } });
    partBase[p.id] = { pos: o.position.clone(), mats };
    partCenter[p.id] = new THREE.Box3().setFromObject(o).getCenter(new THREE.Vector3());
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
  if (focus.target) {
    const dir = camera.position.clone().sub(controls.target).normalize();
    controls.target.lerp(focus.target, 0.12);
    const want = controls.target.clone().add(dir.multiplyScalar(focus.dist));
    camera.position.lerp(want, 0.12);
    if (controls.target.distanceTo(focus.target) < modelSize * 1e-3) focus.target = null;
  }
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
      const inter = status !== 'current' && interPartners.has(pid);
      o.visible = inter || !(status === 'future' && hideFuture);
      for (const m of b.mats) {
        if (inter) {
          m.color.copy(m.userData.base).lerp(COL_INTER, 0.75); m.emissive?.copy(COL_INTER).multiplyScalar(0.15 + 0.2 * pulse);
          m.transparent = true; m.opacity = 0.55; m.depthWrite = false;
        } else if (status === 'current') {
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
renderer.domElement.addEventListener('pointerdown', (e) => { downAt = [e.clientX, e.clientY]; focus.target = null; });
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
  const nrev = state.steps.filter((s) => s.review).length, nb = $('nextReviewBtn');
  nb.hidden = !nrev; nb.textContent = `⚠ проверить: ${nrev} ›`;
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

// автокадрирование: камера плавно наводится на детали текущего шага (вместе с уже собранными)
const focus = { target: null, dist: null };
function focusStep() {
  if (!modelRoot || !$('autoFocus').checked) return;
  const box = new THREE.Box3();
  state.steps.slice(0, state.sel + 1).forEach((s) => {
    for (const pid of [...s.parts, ...(s.fasteners || []).flatMap((f) => f.partIds)]) {
      const o = partObj[pid]; if (o) box.expandByObject(o);
    }
  });
  if (box.isEmpty()) return;
  const size = box.getSize(new THREE.Vector3()).length();
  focus.target = box.getCenter(new THREE.Vector3());
  focus.dist = Math.max(size * 1.4, modelSize * 0.15);
}

function select(i) {
  state.sel = Math.max(0, Math.min(i, state.steps.length - 1));
  focusStep(); updateInter(); updateArrows();
  renderSteps(); renderDetail();
  document.querySelector('#stepList li.sel')?.scrollIntoView({ block: 'nearest' });
}

function markDirty(kind) { state.dirty = true; $('saveBtn').disabled = false; setStatus('Есть несохранённые изменения'); histPush(kind); }

// ---------- история правок (Ctrl+Z / Ctrl+Shift+Z) ----------
// kind: правки одного текстового поля подряд склеиваются в одну запись
const hist = { stack: [], pos: -1, kind: null, at: 0 };
const snapshot = () => JSON.stringify({ steps: state.steps, hints: state.hints, sel: state.sel });
function histReset() { hist.stack = [snapshot()]; hist.pos = 0; hist.kind = null; updateUndo(); }
function histPush(kind) {
  const now = Date.now(), snap = snapshot();
  if (kind && kind === hist.kind && now - hist.at < 2000 && hist.pos > 0) hist.stack[hist.pos] = snap;
  else {
    hist.stack.splice(hist.pos + 1); hist.stack.push(snap); hist.pos = hist.stack.length - 1;
    if (hist.stack.length > 300) { hist.stack.shift(); hist.pos--; }
  }
  hist.kind = kind || null; hist.at = now; updateUndo();
}
function histGo(delta) {
  const to = hist.pos + delta; if (to < 0 || to >= hist.stack.length) return;
  // показать шаг, где была отменяемая (или возвращаемая) правка
  const where = JSON.parse(hist.stack[delta < 0 ? hist.pos : to]).sel;
  hist.pos = to; hist.kind = null;
  const s = JSON.parse(hist.stack[to]); state.steps = s.steps; state.hints = s.hints;
  state.dirty = true; $('saveBtn').disabled = false; updateUndo();
  select(Math.min(where ?? state.sel, state.steps.length - 1));
  setStatus(delta < 0 ? 'Отменено' : 'Возвращено');
}
function updateUndo() { $('undoBtn').disabled = hist.pos <= 0; $('redoBtn').disabled = hist.pos >= hist.stack.length - 1; }

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
  if (s.review) d.appendChild(reviewBox(s, el));
  const field = (label, input, extra) => { const f = el(`<div class="field"><label class="l"></label></div>`); f.querySelector('label').textContent = label; f.appendChild(input); if (extra) f.appendChild(extra); d.appendChild(f); return input; };

  const title = field('Название шага', el('<input type="text">'));
  title.value = s.title; title.oninput = () => { s.title = title.value; s.autoTitle = false; markDirty('title' + s.hintKey); renderStepsKeepFocus(); };

  for (const lang of ['ru', 'kk', 'en']) {
    const ta = el('<textarea rows="2"></textarea>'); const cnt = el('<div class="counter"></div>');
    const upd = () => { const L = ta.value.length; cnt.textContent = `${L}/80`; cnt.classList.toggle('over', L > 80); };
    field(`Подсказка на очках (${lang})`, ta, cnt);
    ta.value = state.hints[lang]?.[s.hintKey] || ''; upd();
    ta.oninput = () => { (state.hints[lang] ||= {})[s.hintKey] = ta.value; upd(); markDirty(`hint-${lang}-${s.hintKey}`); if (lang === 'ru') { renderStepsKeepFocus(); } };
  }

  d.appendChild(dirEditor(s, s.parts, 'Откуда ставится деталь'));

  const tool = field('Инструмент', el('<input type="text" placeholder="например, шестигранник 5 мм">'));
  tool.value = s.tool || ''; tool.oninput = () => { s.tool = tool.value; markDirty('tool' + s.hintKey); };

  const checks = field('Проверки (по одной в строке)', el('<textarea rows="2" placeholder="Прокладка уложена в паз"></textarea>'));
  checks.value = (s.checks || []).join('\n'); checks.oninput = () => { s.checks = checks.value.split('\n').map((x) => x.trim()).filter(Boolean); markDirty('checks' + s.hintKey); };

  if ((s.fasteners || []).length) {
    const box = el('<div class="field"><label class="l">Крепёж</label></div>');
    s.fasteners.forEach((f, fi) => {
      const row = el(`<div class="fastener"><b></b> ×${f.qty}<br>момент, Н·м: <input type="number" step="0.1" min="0"> <button class="ghost" title="Сделать отдельным шагом">в отдельный шаг</button>
        <div class="fdir"></div><div class="fmove"></div></div>`);
      f.partPaths ||= {};
      row.querySelector('.fdir').appendChild(dirSelect(f.partPaths, f.partIds, (dir) => { setPaths(f.partPaths, f.partIds, dir); markDirty(); updateArrows(); }));
      row.querySelector('.fmove').appendChild(stepSelect(i, 'перенести в шаг…', (to) => moveFastener(i, fi, to)));
      row.querySelector('b').textContent = f.name;
      const inp = row.querySelector('input'); inp.value = f.torqueNm ?? '';
      inp.oninput = () => { f.torqueNm = inp.value === '' ? null : +inp.value; markDirty(`torque${s.hintKey}-${fi}`); };
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
    if (interOf(pid).length) { lb.classList.add('inter'); lb.title = 'Пересекается в CAD с: ' + interOf(pid).map((q) => n[q] || q).join(', '); }
    lb.appendChild(stepSelect(i, '→ в шаг…', (to) => movePart(pid, i, to)));
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

// ---------- проверка шагов: пересечения, направления, перенос деталей ----------
const DIRS = [
  ['сверху', [0, 1, 0]], ['снизу', [0, -1, 0]], ['спереди', [0, 0, 1]],
  ['сзади', [0, 0, -1]], ['справа', [1, 0, 0]], ['слева', [-1, 0, 0]],
];
const MIN_DIST_MM = 40;
let interPartners = new Set();
const stepParts = (s) => [...s.parts, ...(s.fasteners || []).flatMap((f) => f.partIds)];
const interOf = (pid) => (state.project?.stats?.interferences || []).filter((p) => p.includes(pid)).map(([a, b]) => (a === pid ? b : a));

function updateInter() {
  const s = state.steps[state.sel]; interPartners = new Set();
  if (!s) return;
  const own = new Set(stepParts(s));
  for (const pid of own) for (const q of interOf(pid)) if (!own.has(q)) interPartners.add(q);
}

function dirKey(path) {
  if (!path || !path[0]) return null;
  const v = path[0], L = Math.hypot(...v); if (L < 1e-9) return null;
  const u = v.map((x) => x / L);
  const hit = DIRS.find(([, d]) => d.every((c, k) => Math.abs(c - u[k]) < 1e-3));
  return hit ? hit[0] : 'своё';
}
const pathLenMm = (path) => (path && path[0] ? Math.round(Math.hypot(...path[0]) * 1000) : 0);

function setPaths(paths, pids, dir, distMm) {
  for (const pid of pids) {
    const cur = paths[pid];
    const mm = Math.max(MIN_DIST_MM, distMm ?? (pathLenMm(cur) || 60));
    paths[pid] = dir ? [dir.map((c) => +(c * mm / 1000).toFixed(5)), [0, 0, 0]] : null;
  }
}

function dirEditor(s, pids, label) {
  const wrap = document.createElement('div'); wrap.className = 'field';
  if (!pids.length) return wrap;
  s.partPaths ||= {};
  const keys = new Set(pids.map((p) => dirKey(s.partPaths[p])));
  const curKey = keys.size === 1 ? [...keys][0] : 'разные';
  wrap.innerHTML = `<label class="l"></label><div class="dirgrid"></div><div class="dirmeta"></div>`;
  wrap.querySelector('label').textContent = label + (pids.length > 1 ? ` (все ${pids.length})` : '');
  const grid = wrap.querySelector('.dirgrid');
  for (const [name, d] of DIRS) {
    const b = document.createElement('button'); b.textContent = name; b.className = curKey === name ? 'on' : '';
    b.title = `Деталь приходит ${name} и движется к месту установки`;
    b.onclick = () => {
      setPaths(s.partPaths, pids, d); s.insertPath = s.partPaths[s.parts[0]] ?? null;
      markDirty(); updateArrows(); renderDetail();
    };
    grid.appendChild(b);
  }
  const meta = wrap.querySelector('.dirmeta');
  const mm = pathLenMm(s.partPaths[pids[0]]);
  if (curKey === null) meta.innerHTML = '<span class="nodir">Путь не задан: на очках деталь просто появится. Выберите направление.</span>';
  else {
    meta.innerHTML = `<span></span> ход, мм: <input type="number" min="${MIN_DIST_MM}" step="10">`;
    meta.querySelector('span').textContent = curKey === 'своё' ? 'Направление из CAD (наклонное).' : curKey === 'разные' ? 'У деталей разные направления.' : '';
    const inp = meta.querySelector('input'); inp.value = mm || '';
    inp.onchange = () => {
      const v = Math.max(MIN_DIST_MM, +inp.value || MIN_DIST_MM);
      for (const p of pids) {
        const cur = s.partPaths[p]; if (!cur) continue;
        const L = Math.hypot(...cur[0]) || 1; s.partPaths[p] = [cur[0].map((c) => +(c / L * v / 1000).toFixed(5)), [0, 0, 0]];
      }
      s.insertPath = s.partPaths[s.parts[0]] ?? null; markDirty(); updateArrows(); renderDetail();
    };
  }
  return wrap;
}

function dirSelect(paths, pids, onPick) {
  const sel = document.createElement('select'); sel.title = 'Откуда ставится крепёж';
  const keys = new Set(pids.map((p) => dirKey(paths?.[p])));
  const cur = keys.size === 1 ? [...keys][0] : 'разные';
  const opt = (v, t) => { const o = document.createElement('option'); o.value = v; o.textContent = t; sel.appendChild(o); };
  opt('', `направление: ${cur ?? 'не задано'}`);
  for (const [name] of DIRS) opt(name, name);
  sel.onchange = () => { const d = DIRS.find(([n]) => n === sel.value); if (d) onPick(d[1]); };
  return sel;
}

function stepSelect(from, placeholder, onPick) {
  const sel = document.createElement('select');
  const o0 = document.createElement('option'); o0.value = ''; o0.textContent = placeholder; sel.appendChild(o0);
  state.steps.forEach((t, j) => {
    if (j === from) return;
    const o = document.createElement('option'); o.value = j; o.textContent = `${j + 1}. ${t.title}`.slice(0, 60); sel.appendChild(o);
  });
  sel.onclick = (e) => e.stopPropagation();
  sel.onchange = () => { if (sel.value !== '') onPick(+sel.value); };
  return sel;
}

function dropIfEmpty(i, into) {
  // шаг без деталей не нужен: его крепёж и проверки уходят в шаг, куда ушла последняя деталь
  const a = state.steps[i];
  if (a.parts.length) { a.insertPath = a.partPaths?.[a.parts[0]] ?? null; a.camera = { ...(a.camera || {}), focus: a.parts[0] }; return into; }
  const b = state.steps[into];
  b.fasteners = [...(b.fasteners || []), ...(a.fasteners || [])];
  b.checks = [...(b.checks || []), ...(a.checks || [])];
  b.review = b.review || a.review;
  state.steps.splice(i, 1);
  return into > i ? into - 1 : into;
}

function movePart(pid, from, to) {
  const a = state.steps[from], b = state.steps[to];
  a.parts = a.parts.filter((p) => p !== pid); b.parts.push(pid);
  b.partPaths = { ...(b.partPaths || {}), [pid]: a.partPaths?.[pid] ?? null };
  if (a.partPaths) delete a.partPaths[pid];
  if (!b.insertPath) b.insertPath = b.partPaths[b.parts[0]] ?? null;
  const at = dropIfEmpty(from, to);
  relabel(); markDirty(); select(at);
  setStatus(`Деталь перенесена в шаг ${at + 1}`, 'ok');
}

function moveFastener(from, fi, to) {
  const a = state.steps[from], b = state.steps[to];
  const [f] = a.fasteners.splice(fi, 1);
  const same = (b.fasteners || []).find((g) => g.name === f.name);
  if (same) { same.partIds.push(...f.partIds); same.qty = same.partIds.length; same.partPaths = { ...same.partPaths, ...f.partPaths }; }
  else (b.fasteners ||= []).push(f);
  relabel(); markDirty(); select(to);
  setStatus(`Крепёж перенесён в шаг ${to + 1}`, 'ok');
}

function reviewBox(s, el) {
  const n = names();
  const r = el(`<div class="review"><b>⚠ Нужно проверить: <span class="why"></span></b><div class="body"></div>
    <button>Проверено, всё верно</button></div>`);
  r.querySelector('.why').textContent = s.review;
  const body = r.querySelector('.body');
  const pairs = [];
  for (const pid of stepParts(s)) for (const q of interOf(pid)) pairs.push(`${n[pid] || pid} ↔ ${n[q] || q}`);
  if (pairs.length) {
    const p = document.createElement('p'); p.textContent = 'В CAD эти детали заходят друг в друга (красные на модели):'; body.appendChild(p);
    const ul = document.createElement('ul'); for (const t of pairs) { const li = document.createElement('li'); li.textContent = t; ul.appendChild(li); } body.appendChild(ul);
    const p2 = document.createElement('p');
    p2.textContent = 'Обычно это посадка с натягом, резьба или неточная модель, и сборке не мешает. Посмотрите анимацию: если деталь ставится не оттуда, выберите направление ниже, при необходимости переставьте шаг. Затем нажмите «Проверено».';
    body.appendChild(p2);
  } else {
    const p = document.createElement('p');
    p.textContent = 'Studio не нашла прямого пути, по которому деталь можно поставить. Выберите направление ниже и проверьте порядок шагов.';
    body.appendChild(p);
  }
  r.querySelector('button').onclick = () => {
    s.review = null; markDirty(); updateInter(); renderSteps(); renderDetail();
    const left = state.steps.filter((x) => x.review).length;
    setStatus(left ? `Осталось проверить шагов: ${left}` : 'Все шаги проверены', 'ok');
  };
  return r;
}

function updateArrows() {
  // стрелки «откуда приходит деталь» для текущего шага
  while (arrows.children.length) { const a = arrows.children.pop(); a.traverse?.((m) => { m.geometry?.dispose(); m.material?.dispose(); }); }
  const s = state.steps[state.sel]; if (!s || !modelRoot) return;
  for (const pid of stepParts(s)) {
    const path = pathOf(s, pid), c = partCenter[pid]; if (!path || !c) continue;
    const v = new THREE.Vector3(...path[0]); const L = v.length(); if (L < 1e-6) continue;
    const isFast = !s.parts.includes(pid);
    const len = Math.max(L, modelSize * 0.12);
    const origin = c.clone().add(v.clone().normalize().multiplyScalar(len));
    const arrow = new THREE.ArrowHelper(v.clone().normalize().negate(), origin, len * 0.92, isFast ? 0xff8a1f : 0x1a8cff, len * 0.22, len * 0.12);
    arrow.traverse((m) => { if (m.material) { m.material.depthTest = false; m.material.transparent = true; m.material.opacity = 0.85; } m.renderOrder = 10; });
    arrows.add(arrow);
  }
}

function nextReview() {
  const n = state.steps.length;
  for (let k = 1; k <= n; k++) { const j = (state.sel + k) % n; if (state.steps[j].review) { select(j); return; } }
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
  for (const id of ['hintsBtn', 'replanBtn', 'exportBtn', 'sendBtn']) $(id).disabled = false;
  $('emptyState').hidden = true;
  if (!keepSel) state.sel = 0;
  await loadModel(data.id);
  histReset();
  focusStep(); updateInter(); updateArrows();
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
$('undoBtn').onclick = () => histGo(-1);
$('redoBtn').onclick = () => histGo(1);
$('nextReviewBtn').onclick = nextReview;
document.addEventListener('keydown', (e) => {
  if (['INPUT', 'TEXTAREA', 'SELECT'].includes(document.activeElement?.tagName)) return;
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'z') { e.preventDefault(); histGo(e.shiftKey ? 1 : -1); return; }
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'y') { e.preventDefault(); histGo(1); return; }
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') { e.preventDefault(); guarded(save, 'Сохранение…'); return; }
  if (e.key === 'ArrowRight' || e.key === 'ArrowDown') { e.preventDefault(); select(state.sel + 1); }
  if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') { e.preventDefault(); select(state.sel - 1); }
});
// ---------- передача на очки ----------
const link = { status: null, addr: 0, timer: null };
const dlg = $('linkDlg');

function renderLink() {
  const st = link.status; if (!st) return;
  const paired = st.devices.some((d) => d.online);
  $('linkDot').className = 'dot' + (paired ? ' paired' : st.running ? ' on' : '');
  $('linkDot').title = paired ? 'Очки на связи' : st.running ? 'Передача включена, очки не подключены' : 'Передача выключена';
  $('linkOff').hidden = st.running; $('linkOn').hidden = !st.running;
  if (!st.running) return;
  link.addr = Math.min(link.addr, st.urls.length - 1);
  const sel = $('linkAddr');
  sel.hidden = st.urls.length < 2;
  if (sel.options.length !== st.urls.length) {
    sel.innerHTML = ''; st.urls.forEach((u, k) => { const o = document.createElement('option'); o.value = k; o.textContent = u.replace('http://', 'адрес: '); sel.appendChild(o); });
  }
  sel.value = link.addr;
  const qr = `/api/link/qr.svg?i=${link.addr}&k=${encodeURIComponent(st.pairUrls[link.addr])}`;
  if ($('qrImg').dataset.src !== qr) { $('qrImg').src = qr; $('qrImg').dataset.src = qr; }
  $('pairUrl').textContent = st.pairUrls[link.addr];
  const c = st.current;
  $('linkCurrent').innerHTML = c ? `На очки отправлено: <b></b><br><span class="muted"></span>` : '<span class="muted">Пакет ещё не отправлен.</span>';
  if (c) { $('linkCurrent').querySelector('b').textContent = c.name; $('linkCurrent').querySelector('span').textContent = `версия ${c.version} · шагов ${c.steps} · ${c.sentAt.replace('T', ' ')}`; }
  const dv = $('linkDevices'); dv.innerHTML = '';
  if (!st.devices.length) dv.innerHTML = '<span class="muted">Очки ещё не подключались.</span>';
  for (const d of st.devices) {
    const row = document.createElement('div');
    const got = c && d.version === c.version ? ' · <span class="ok">загрузили последнюю версию</span>' : d.version ? ` · загружена версия ${d.version}` : '';
    row.innerHTML = `<b></b> <span class="muted"></span>${got}`;
    row.querySelector('b').textContent = d.online ? '● ' + d.name : '○ ' + d.name;
    row.querySelector('span').textContent = d.online ? '(на связи)' : `(был ${fmtAgo(d.secondsAgo)} назад)`;
    dv.appendChild(row);
  }
}
const fmtAgo = (s) => (s < 60 ? `${s} с` : s < 3600 ? `${Math.round(s / 60)} мин` : `${Math.round(s / 3600)} ч`);

async function pollLink() {
  try { link.status = await api('/api/link'); renderLink(); } catch { /* сервер перезапускается */ }
}
function openLink() {
  pollLink(); dlg.showModal();
}
setInterval(pollLink, 3000); pollLink();
$('linkAddr').onchange = (e) => { link.addr = +e.target.value; renderLink(); };
$('linkBtn').onclick = openLink;
$('linkStart').onclick = () => guarded(async () => { link.status = await api('/api/link/start', { method: 'POST', headers: { 'content-type': 'application/json' }, body: '{}' }); renderLink(); }, 'Включаю…');
$('linkStop').onclick = () => guarded(async () => { link.status = await api('/api/link/stop', { method: 'POST' }); renderLink(); }, 'Выключаю…');
$('linkReset').onclick = () => {
  if (!confirm('Сделать новый QR-код? Уже подключённые очки потеряют доступ, их нужно будет подключить заново.')) return;
  guarded(async () => { link.status = await api('/api/link/reset', { method: 'POST' }); renderLink(); }, 'Новый код…');
};

async function sendToGlasses() {
  if (!state.project) { setStatus('Сначала откройте проект', 'err'); return; }
  if (state.dirty) await save();
  try {
    const r = await api(`/api/projects/${state.project.id}/send`, { method: 'POST' });
    link.status = r.link; renderLink();
    const rev = state.steps.filter((s) => s.review).length;
    setStatus(`Отправлено на очки: версия ${r.sent.version}` + (rev ? `. Внимание: не проверено шагов: ${rev}` : '') +
      (r.warnings.length ? `. Предупреждения: ${r.warnings.join('; ')}` : ''), 'ok');
    if (!r.link.devices.length && !dlg.open) dlg.showModal(); // очки ещё не подключали: показать QR
  } catch (e) {
    if (e.body?.errors) throw new Error('Не отправлено: ' + e.body.errors.join('; '));
    throw e;
  }
}
$('sendBtn').onclick = () => guarded(sendToGlasses, 'Отправка на очки…');
$('linkSend').onclick = () => guarded(sendToGlasses, 'Отправка на очки…');

window.addEventListener('beforeunload', (e) => { if (state.dirty) { e.preventDefault(); e.returnValue = ''; } });

(async () => {
  try {
    const list = await refreshList();
    const last = localStorageGet('stark.lastProject');
    const pick = list.find((p) => p.id === last) || null;
    if (pick) await guarded(async () => openProject(await api(`/api/projects/${pick.id}`)), 'Загрузка…');
  } catch (e) { setStatus('Сервер недоступен: ' + e.message, 'err'); }
})();
