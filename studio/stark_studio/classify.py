"""Классификация деталей: крепёж (болты, винты, гайки, шайбы) по имени и форме."""
from __future__ import annotations

import re

import numpy as np

from .geometry import PartGeom

FASTENER_RE = re.compile(
    r"(болт|винт|гайк|шайб|шпильк|заклёп|заклеп|саморез|штифт|"
    r"bolt|screw|nut|washer|rivet|stud|pin\b|"
    r"\bdin\s?(912|933|931|934|125|7991|7984)|\biso\s?(4762|4017|4014|4032|7089|10642)|\bM\d{1,2}(x\d+)?\b)",
    re.IGNORECASE,
)


NOT_FASTENER_RE = re.compile(r"(вал|ось|shaft|axle|spindle|шпиндел|ролик|roller|rod|штанг|тяг)", re.IGNORECASE)


def is_fastener_name(name: str) -> bool:
    return bool(FASTENER_RE.search(name or ""))


def is_fastener_shape(g: PartGeom, assembly_diag: float) -> bool:
    """Маленькая вытянутая деталь (длина ≥ 2 диаметра, < 12% размера сборки)."""
    e = np.sort(g.extents)
    if e[2] <= 0:
        return False
    small = e[2] < 0.12 * assembly_diag
    elongated = e[2] >= 2.0 * e[1] and abs(e[1] - e[0]) < 0.25 * e[1] + 1e-6
    return bool(small and elongated)


def classify(parts, geoms: dict[str, PartGeom]) -> dict[str, list[str]]:
    lo = np.min([g.aabb[0] for g in geoms.values()], axis=0)
    hi = np.max([g.aabb[1] for g in geoms.values()], axis=0)
    diag = float(np.linalg.norm(hi - lo))
    tags = {}
    for p in parts:
        t = []
        if is_fastener_name(p.name) or (is_fastener_shape(geoms[p.id], diag) and not NOT_FASTENER_RE.search(p.name or "")):
            t.append("fastener")
        tags[p.id] = t
    return tags
