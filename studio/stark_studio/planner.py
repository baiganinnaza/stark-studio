"""Планировщик v0: «сборка через разборку» с проверкой столкновений (FCL).

Повторяем, пока есть детали: ищем детали, которые можно вынуть по прямой без столкновений,
выбираем по правилам (крепёж → верхние → мелкие), вынимаем. Порядок разборки в обратную
сторону = порядок сборки, направление извлечения в обратную сторону = путь установки.
"""
from __future__ import annotations

from dataclasses import dataclass, field

import fcl
import numpy as np

from .geometry import PartGeom, shrink_mesh

AXES = [np.array(v, float) for v in ([0, 0, 1], [1, 0, 0], [-1, 0, 0], [0, 1, 0], [0, -1, 0], [0, 0, -1])]


@dataclass
class Removal:
    part_ids: list[str]
    direction: np.ndarray | None      # мир (мм, Z вверх); None = не вынимается
    distances: dict[str, float]       # на сколько мм вынуть деталь, чтобы она стала свободной
    review: str | None = None         # причина ручной проверки


@dataclass
class PlanResult:
    removals: list[Removal]           # порядок разборки
    interferences: list[tuple[str, str]] = field(default_factory=list)
    contacts: dict[str, set[str]] = field(default_factory=dict)


class CollisionWorld:
    def __init__(self, geoms: dict[str, PartGeom], tol: float):
        self.geoms = geoms
        self.ignore: set[frozenset] = set()
        self.objs = {}
        for pid, g in geoms.items():
            if len(g.faces) == 0:
                continue
            v = shrink_mesh(g.world_vertices, g.faces, tol)
            m = fcl.BVHModel()
            m.beginModel(len(v), len(g.faces))
            m.addSubModel(v.astype(np.float64), g.faces.astype(np.int32))
            m.endModel()
            self.objs[pid] = fcl.CollisionObject(m, fcl.Transform())

    def collide(self, a: str, offset: np.ndarray, b: str) -> bool:
        oa, ob = self.objs.get(a), self.objs.get(b)
        if oa is None or ob is None:
            return False
        oa.setTransform(fcl.Transform(offset.astype(np.float64)))
        req, res = fcl.CollisionRequest(), fcl.CollisionResult()
        hit = fcl.collide(oa, ob, req, res) > 0
        oa.setTransform(fcl.Transform())
        return hit


    def distance(self, a: str, b: str) -> float:
        oa, ob = self.objs.get(a), self.objs.get(b)
        if oa is None or ob is None:
            return float("inf")
        req, res = fcl.DistanceRequest(), fcl.DistanceResult()
        return float(fcl.distance(oa, ob, req, res))


def contact_graph(world: "CollisionWorld", gap: float) -> dict[str, set[str]]:
    """Какие детали касаются друг друга (расстояние между сетками ≤ gap мм)."""
    ids = list(world.geoms)
    adj = {a: set() for a in ids}
    pad = np.array([[-gap] * 3, [gap] * 3])
    for i, a in enumerate(ids):
        for b in ids[i + 1:]:
            if _aabb_overlap(world.geoms[a].aabb + pad, world.geoms[b].aabb) and world.distance(a, b) <= gap:
                adj[a].add(b)
                adj[b].add(a)
    return adj


def _aabb_overlap(a: np.ndarray, b: np.ndarray, eps: float = 0.0) -> bool:
    return bool(np.all(a[0] < b[1] - eps) and np.all(b[0] < a[1] - eps))


def _escape_distance(ga: PartGeom, others: list[PartGeom], d: np.ndarray, margin: float) -> float:
    """Насколько сдвинуть деталь по d, чтобы её AABB ушёл от всех остальных AABB."""
    best = 0.0
    for gb in others:
        # проекции на направление
        a_min = np.dot(np.where(d > 0, ga.aabb[0], ga.aabb[1]), d)
        b_max = np.dot(np.where(d > 0, gb.aabb[1], gb.aabb[0]), d)
        best = max(best, b_max - a_min)
    return max(best, 0.0) + margin


def removable(world: CollisionWorld, pid: str, remaining: list[str], d: np.ndarray,
              margin: float = 5.0) -> float | None:
    """Возвращает дистанцию извлечения (мм) или None, если по направлению d деталь упирается."""
    ga = world.geoms[pid]
    others = [world.geoms[o] for o in remaining if o != pid]
    if not others:
        return margin
    dist = _escape_distance(ga, others, d, margin)
    # широкая фаза: кто вообще попадает в заметаемый объём
    swept = np.array([np.minimum(ga.aabb[0], ga.aabb[0] + d * dist), np.maximum(ga.aabb[1], ga.aabb[1] + d * dist)])
    # пары, которые пересекаются уже в собранном виде (зацепление шестерён, запрессовка, неточный CAD),
    # между собой не проверяем: иначе ни одну из них нельзя снять. Такие шаги помечаются «проверить».
    cand = [o for o in remaining if o != pid and frozenset((pid, o)) not in world.ignore
            and _aabb_overlap(swept, world.geoms[o].aabb, 1e-6)]
    if not cand:
        return dist
    thin = min([np.min(ga.extents)] + [np.min(world.geoms[o].extents) for o in cand])
    step = float(np.clip(thin / 3.0, 0.5, 10.0))
    n = int(np.ceil(dist / step))
    if n > 600:
        n, step = 600, dist / 600
    for i in range(1, n + 1):
        off = d * (i * step)
        box = ga.aabb + off
        for o in cand:
            if _aabb_overlap(box, world.geoms[o].aabb) and world.collide(pid, off, o):
                return None
    return dist


def symmetry_axis(g: PartGeom) -> np.ndarray | None:
    """Ось вращательной симметрии (болт, гайка, шайба): главная ось инерции с «непохожим» моментом."""
    import trimesh
    try:
        m = trimesh.Trimesh(g.world_vertices, g.faces, process=False)
        c, v = m.principal_inertia_components, m.principal_inertia_vectors
        if not np.all(np.isfinite(c)) or np.max(np.abs(c)) == 0:
            raise ValueError
        best, gap = None, 0.0
        for i in range(3):
            j, k = [x for x in range(3) if x != i]
            if abs(c[j] - c[k]) < 0.1 * max(abs(c[j]), 1e-9):
                g_ = min(abs(c[i] - c[j]), abs(c[i] - c[k])) / max(abs(c).max(), 1e-9)
                if g_ > gap:
                    best, gap = v[i], g_
        if best is not None and gap > 0.02:
            a = np.asarray(best, float)
            # прилипаем к мировой оси, если почти совпадает
            k = np.argmax(np.abs(a))
            if abs(a[k]) > 0.99:
                a = np.eye(3)[k] * np.sign(a[k])
            return a / np.linalg.norm(a)
    except Exception:
        pass
    # запасной путь по габаритам: две стороны почти равны (шестигранник: до 1.16 раза), третья отличается
    e = np.asarray(g.extents, float)
    for i in range(3):
        j, k = [x for x in range(3) if x != i]
        lo, hi = sorted((e[j], e[k]))
        if hi <= 1.3 * lo and (e[i] > 1.5 * hi or e[i] < 0.6 * lo):
            return np.eye(3)[i]
    return None


def _shape_signature(g: PartGeom) -> tuple:
    return (len(g.faces), tuple(np.round(np.sort(g.extents), 1)), round(g.volume, 0))


def plan(geoms: dict[str, PartGeom], tags: dict[str, list[str]], local_dirs: dict[str, list[np.ndarray]] | None = None,
         tol: float = 0.15, up=np.array([0, 0, 1.0])) -> PlanResult:
    world = CollisionWorld(geoms, tol)
    remaining = [pid for pid in geoms]
    result = PlanResult(removals=[])

    # интерференции в исходной модели (запрессовка, ошибки CAD)
    for i, a in enumerate(remaining):
        for b in remaining[i + 1:]:
            if _aabb_overlap(geoms[a].aabb, geoms[b].aabb) and world.collide(a, np.zeros(3), b):
                result.interferences.append((a, b))
    interfering = {x for pair in result.interferences for x in pair}
    world.ignore = {frozenset(p) for p in result.interferences}

    sig = {pid: _shape_signature(g) for pid, g in geoms.items()}

    # граф касаний: после снятия детали остальные не должны «висеть в воздухе»
    # (в порядке сборки каждая деталь опирается на уже установленные)
    adj = contact_graph(world, gap=2 * tol + 0.5)
    result.contacts = adj

    def stays_connected(removed: set[str], rest: list[str]) -> bool:
        nodes = [r for r in rest if r not in removed and "fastener" not in tags.get(r, [])]
        if len(nodes) <= 1:
            return True
        seen, stack = {nodes[0]}, [nodes[0]]
        allowed = set(nodes)
        while stack:
            x = stack.pop()
            for y in adj[x]:
                if y in allowed and y not in seen:
                    seen.add(y)
                    stack.append(y)
        return len(seen) == len(nodes)

    sym = {pid: symmetry_axis(geoms[pid]) for pid in geoms if "fastener" in tags.get(pid, [])}

    def dirs_for(pid):
        ds = list(AXES)
        if pid in sym and sym[pid] is not None:
            # крепёж снимается вдоль своей оси (болт вверх из отверстия, гайка с болта)
            a = sym[pid]
            pair = [a, -a] if a @ up >= 0 else [-a, a]
            ds = pair + [x for x in ds if all(np.linalg.norm(x - y) > 1e-3 for y in pair)]
        for v in (local_dirs or {}).get(pid, []):
            v = v / (np.linalg.norm(v) + 1e-12)
            for s in (v, -v):
                if all(np.linalg.norm(s - x) > 1e-3 for x in ds):
                    ds.insert(len(ds) - 1, s)  # перед -Z: вниз (в стол) в последнюю очередь
        return ds

    while remaining:
        free: dict[str, tuple[np.ndarray, float]] = {}
        for pid in remaining:
            for d in dirs_for(pid):
                if len(remaining) > 1 and d @ up < -0.99 and pid not in sym:
                    # вниз сквозь стол можно только крепежу (гайки снизу) или если больше никак
                    continue
                dist = removable(world, pid, remaining, d)
                if dist is not None:
                    free[pid] = (d, dist)
                    break
        if not free:
            # пробуем «вниз», потом сдаёмся и помечаем на ручную проверку
            for pid in remaining:
                dist = removable(world, pid, remaining, -up)
                if dist is not None:
                    free[pid] = (-up, dist)
        if not free:
            top = max(remaining, key=lambda p: geoms[p].aabb[1] @ up)
            reason = "интерференция в CAD" if top in interfering else "не вынимается по прямой"
            result.removals.append(Removal([top], None, {top: 0.0}, review=reason))
            remaining.remove(top)
            continue

        def encloses(pid):
            # корпус, внутри которого ещё есть детали: снимаем позже, ставим раньше содержимого
            box = geoms[pid].aabb
            for o in remaining:
                if o == pid or "fastener" in tags.get(o, []) or geoms[o].volume >= geoms[pid].volume:
                    continue
                ob = geoms[o].aabb
                if np.all(ob[0] >= box[0] - 0.5) and np.all(ob[1] <= box[1] + 0.5):
                    return True
            return False

        def score(pid):
            g = geoms[pid]
            fast = "fastener" in tags.get(pid, [])
            e = np.sort(g.extents)
            elongated = e[2] >= 1.8 * e[1]
            # сначала гайки/шайбы, потом болты (резьбу планировщик не видит), потом детали
            return (0 if fast else 1, (1 if elongated else 0) if fast else 0,
                    0 if stays_connected({pid}, remaining) else 1,
                    1 if encloses(pid) else 0,
                    -round(float(g.aabb[1] @ up), 1), g.volume)

        best = min(free, key=score)
        d_best = free[best][0]
        def local(pid, d):
            axes = (local_dirs or {}).get(pid)
            if not axes:
                return np.round(d, 2)
            return np.round(np.array([a / (np.linalg.norm(a) + 1e-12) @ d for a in axes]), 2)

        group = [best]
        for pid, (d, dist) in free.items():
            if pid == best or sig[pid] != sig[best]:
                continue
            # одинаковые детали в один шаг, если снимаются в одну сторону (в мире или в своей системе координат)
            # (зеркальные детали: противоположные направления в мире, одинаковые в своей системе)
            same_dir = np.allclose(np.round(d, 2), np.round(d_best, 2)) or (
                np.allclose(np.round(d, 2), -np.round(d_best, 2)) and np.allclose(local(pid, d), local(best, d_best)))
            if same_dir:
                group.append(pid)
        group.sort()
        dists = {pid: free[pid][1] for pid in group}
        rev = None
        if any(p in interfering for p in group):
            rev = "интерференция в CAD"
        result.removals.append(Removal(group, d_best if len({tuple(np.round(free[p][0], 3)) for p in group}) == 1 else None,
                                       dists, review=rev))
        # направления каждой детали сохраняем отдельно
        result.removals[-1].per_part_dir = {p: free[p][0] for p in group}  # type: ignore[attr-defined]
        for p in group:
            remaining.remove(p)
    return result
