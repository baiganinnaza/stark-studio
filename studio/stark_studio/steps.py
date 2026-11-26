"""Из плана разборки получить шаги сборки в формате steps.json (.stark v1)."""
from __future__ import annotations

import re

import numpy as np

from .geometry import Z_UP_TO_Y_UP
from .planner import PlanResult

R_GLTF = Z_UP_TO_Y_UP[:3, :3]
MIN_INSERT_MM = 40.0  # чтобы анимация установки была заметна на очках


def base_name(name: str) -> str:
    """'Болт M6x58:3' -> 'Болт M6x58'."""
    return re.sub(r"([:#]\d+|_\d+)$", "", name).strip()


def insert_path(direction_mm_world: np.ndarray, dist_mm: float) -> list[list[float]]:
    """Путь установки в координатах glTF (метры, Y вверх) как смещения от итоговой позиции."""
    v = R_GLTF @ (np.asarray(direction_mm_world) * max(dist_mm, MIN_INSERT_MM)) * 0.001
    return [[round(float(x), 5) for x in v], [0.0, 0.0, 0.0]]


def build_steps(plan: PlanResult, names: dict[str, str], tags: dict[str, list[str]]) -> list[dict]:
    groups = list(reversed(plan.removals))
    steps: list[dict] = []
    for g in groups:
        dirs = getattr(g, "per_part_dir", {}) or {}
        paths = {}
        for pid in g.part_ids:
            d = dirs.get(pid, g.direction)
            paths[pid] = insert_path(d, g.distances[pid]) if d is not None else None
        is_fast = all("fastener" in tags.get(p, []) for p in g.part_ids)
        if is_fast:
            target = None
            touching = set().union(*[plan.contacts.get(p, set()) for p in g.part_ids])
            for st in reversed(steps):
                in_step = set(st["parts"]).union(*[set(f["partIds"]) for f in st["fasteners"]])
                if in_step & touching:
                    target = st
                    break
            fname = base_name(names[g.part_ids[0]])
            if target is not None and any(f["name"] != fname for f in target["fasteners"]):
                # второй вид крепежа (болты, потом гайки) отдельным шагом: на очках не больше одного действия
                target = None
            if target is not None:
                if g.review:
                    target["review"] = target.get("review") or g.review
                same = next((f for f in target["fasteners"] if f["name"] == base_name(names[g.part_ids[0]])), None)
                if same is not None:
                    same["partIds"] += list(g.part_ids)
                    same["qty"] = len(same["partIds"])
                    same["partPaths"].update(paths)
                    continue
                target["fasteners"].append({
                    "partIds": list(g.part_ids),
                    "name": base_name(names[g.part_ids[0]]),
                    "qty": len(g.part_ids),
                    "torqueNm": None,
                    "partPaths": paths,
                })
                continue
        steps.append({
            "parts": list(g.part_ids),
            "fasteners": [],
            "insertPath": paths[g.part_ids[0]],
            "partPaths": paths,
            "tool": "",
            "checks": [],
            "camera": {"focus": g.part_ids[0], "explode": 0.0},
            "review": g.review,
            "title": "",
        })
    renumber(steps, names)
    return steps


def default_title(step: dict, names: dict[str, str]) -> str:
    n = base_name(names.get(step["parts"][0], step["parts"][0]))
    qty = len(step["parts"])
    t = f"Установить: {n}" + (f" ×{qty}" if qty > 1 else "")
    for f in step.get("fasteners", []):
        t += f", закрепить {f['name']} ×{f['qty']}"
    return t


def renumber(steps: list[dict], names: dict[str, str]) -> None:
    """id шагов идут по порядку (S01, S02...), а hintKey постоянный, чтобы подсказки не терялись при перестановке."""
    used = {st["hintKey"] for st in steps if st.get("hintKey")}
    n = 1
    for i, st in enumerate(steps, 1):
        st["id"] = f"S{i:02d}"
        if not st.get("hintKey"):
            while f"H{n:03d}" in used:
                n += 1
            st["hintKey"] = f"H{n:03d}"
            used.add(st["hintKey"])
        if not st.get("title") or st.get("autoTitle", True):
            st["title"] = default_title(st, names)
            st["autoTitle"] = True
