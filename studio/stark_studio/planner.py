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
    cand = [o for o in remaining if o != pid and _aabb_overlap(swept, world.geoms[o].aabb, 1e-6)]
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

    def dirs_for(pid):
        ds = list(AXES)
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
                if len(remaining) > 1 and d[2] < -0.99:
                    # вниз сквозь стол можно только если больше никак
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

        def score(pid):
            g = geoms[pid]
            return (0 if "fastener" in tags.get(pid, []) else 1,
                    0 if stays_connected({pid}, remaining) else 1,
                    -round(float(g.aabb[1] @ up), 1), g.volume)

        best = min(free, key=score)
        d_best = free[best][0]
        group = [best]
        for pid, (d, dist) in free.items():
            if pid != best and sig[pid] == sig[best] and ("fastener" in tags.get(pid, [])) == ("fastener" in tags.get(best, [])):
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
