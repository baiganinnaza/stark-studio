"""Нормализация сборки, сетки для коллизий и упрощённые сетки для очков."""
from __future__ import annotations

from dataclasses import dataclass

import numpy as np
import trimesh

from .cad_import import CadAssembly

# Перевод CAD (Z вверх, мм) в glTF (Y вверх, метры): (x, y, z) -> (x, z, -y)
Z_UP_TO_Y_UP = np.array([[1, 0, 0, 0], [0, 0, 1, 0], [0, -1, 0, 0], [0, 0, 0, 1]], dtype=float)
UP_AXES = {"Z": np.eye(4), "Y": np.array([[1, 0, 0, 0], [0, 0, -1, 0], [0, 1, 0, 0], [0, 0, 0, 1]], float)}


@dataclass
class PartGeom:
    id: str
    world_vertices: np.ndarray  # мм, нормализованная сборка (Z вверх, стоит на z=0)
    faces: np.ndarray
    aabb: np.ndarray            # (2,3)
    volume: float
    extents: np.ndarray


def normalize(asm: CadAssembly, up_axis: str = "Z") -> np.ndarray:
    """Матрица, которая приводит сборку к Z вверх, центр XY в 0 и дно на z=0 (мм)."""
    pre = UP_AXES[up_axis.upper()]
    pts = []
    for p in asm.parts:
        v = asm.shapes[p.shape_key].vertices
        if len(v):
            pts.append(trimesh.transform_points(v, pre @ p.transform))
    allp = np.vstack(pts)
    lo, hi = allp.min(0), allp.max(0)
    t = np.eye(4)
    t[:3, 3] = [-(lo[0] + hi[0]) / 2, -(lo[1] + hi[1]) / 2, -lo[2]]
    return t @ pre


def part_geoms(asm: CadAssembly, norm: np.ndarray) -> dict[str, PartGeom]:
    out = {}
    for p in asm.parts:
        s = asm.shapes[p.shape_key]
        v = trimesh.transform_points(s.vertices, norm @ p.transform) if len(s.vertices) else s.vertices
        m = trimesh.Trimesh(v, s.faces, process=True)
        try:
            vol = abs(float(m.volume)) if m.is_volume else float(np.prod(m.extents))
        except Exception:
            vol = float(np.prod(m.extents))
        out[p.id] = PartGeom(id=p.id, world_vertices=np.asarray(m.vertices), faces=np.asarray(m.faces),
                             aabb=np.array(m.bounds), volume=vol, extents=np.asarray(m.extents))
    return out


def shrink_mesh(vertices: np.ndarray, faces: np.ndarray, tol: float) -> np.ndarray:
    """Сжать сетку на tol мм внутрь по нормалям вершин: касание граней не считается столкновением."""
    m = trimesh.Trimesh(vertices, faces, process=False)
    return vertices - m.vertex_normals * tol


def simplify_shapes(asm: CadAssembly, fastener_keys: set[str], budget: int = 200_000) -> dict[str, tuple[np.ndarray, np.ndarray]]:
    """Упростить уникальные сетки так, чтобы вся сборка (с экземплярами) влезла в бюджет треугольников.

    Крепёж упрощается в 4 раза сильнее остальных деталей.
    """
    counts = {}
    for p in asm.parts:
        counts[p.shape_key] = counts.get(p.shape_key, 0) + 1
    total = sum(len(asm.shapes[k].faces) * n for k, n in counts.items())
    result = {k: (s.vertices, s.faces) for k, s in asm.shapes.items()}
    if total <= budget:
        return result
    weight = {k: (0.25 if k in fastener_keys else 1.0) for k in counts}
    weighted = sum(len(asm.shapes[k].faces) * n * weight[k] for k, n in counts.items())
    ratio = budget / weighted
    for k, s in asm.shapes.items():
        target = max(12, int(len(s.faces) * min(1.0, ratio * weight[k])))
        if target >= len(s.faces):
            continue
        m = trimesh.Trimesh(s.vertices, s.faces, process=True)
        try:
            ms = m.simplify_quadric_decimation(face_count=target)
            result[k] = (np.asarray(ms.vertices), np.asarray(ms.faces))
        except Exception:
            pass  # без fast_simplification оставляем как есть, валидатор предупредит
    return result
