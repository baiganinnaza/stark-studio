"""GLB-модель, валидация и упаковка .stark (zip: manifest.json, model.glb, steps.json, hints/*.json)."""
from __future__ import annotations

import io
import json
import zipfile
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
import trimesh
from trimesh.visual.material import PBRMaterial

from .cad_import import CadAssembly
from .geometry import Z_UP_TO_Y_UP

FORMAT_VERSION = 1
TRI_BUDGET = 200_000
TO_GLTF = np.diag([0.001, 0.001, 0.001, 1.0]) @ Z_UP_TO_Y_UP


def build_glb(asm: CadAssembly, norm: np.ndarray, meshes: dict[str, tuple[np.ndarray, np.ndarray]]) -> tuple[bytes, int]:
    """Каждая деталь = узел glTF с именем partId; одинаковые детали делят одну сетку."""
    scene = trimesh.Scene()
    geoms: dict[tuple, trimesh.Trimesh] = {}
    gnames: dict[tuple, str] = {}
    tri = 0
    for p in asm.parts:
        v, f = meshes[p.shape_key]
        rgba = tuple(round(float(c), 3) for c in p.color)
        key = (p.shape_key, rgba)
        if key not in geoms:
            m = trimesh.Trimesh(v * 1.0, f, process=False)
            m.visual = trimesh.visual.TextureVisuals(material=PBRMaterial(
                name=f"{p.shape_key}", baseColorFactor=[*rgba[:3], rgba[3]], metallicFactor=0.1, roughnessFactor=0.6))
            geoms[key] = m
            gnames[key] = f"{p.shape_key}-{len(geoms)}"
        scene.add_geometry(geoms[key], node_name=p.id, geom_name=gnames[key], transform=TO_GLTF @ norm @ p.transform)
        tri += len(f)
    return scene.export(file_type="glb"), tri


def glb_node_names(glb: bytes) -> set[str]:
    s = trimesh.load(io.BytesIO(glb), file_type="glb", force="scene")
    return set(s.graph.nodes_geometry)


def validate(project: dict, glb: bytes) -> tuple[list[str], list[str]]:
    errors, warnings = [], []
    nodes = glb_node_names(glb)
    part_ids = {p["id"] for p in project["parts"]}
    missing = part_ids - nodes
    if missing:
        errors.append(f"В model.glb нет узлов для деталей: {', '.join(sorted(missing))}")
    used: list[str] = []
    for st in project["steps"]:
        used += st["parts"]
        for f in st.get("fasteners", []):
            used += f["partIds"]
        if not st["parts"]:
            errors.append(f"{st['id']}: в шаге нет деталей")
        hint = project["hints"].get("ru", {}).get(st["hintKey"], "").strip()
        if not hint:
            errors.append(f"{st['id']}: нет подсказки (ru)")
        elif len(hint) > 80:
            warnings.append(f"{st['id']}: подсказка длиннее 80 символов")
        if st.get("review"):
            warnings.append(f"{st['id']}: требует ручной проверки ({st['review']})")
    unknown = set(used) - part_ids
    if unknown:
        errors.append(f"Неизвестные детали в шагах: {', '.join(sorted(unknown))}")
    dup = {x for x in used if used.count(x) > 1}
    if dup:
        errors.append(f"Детали в нескольких шагах: {', '.join(sorted(dup))}")
    left = part_ids - set(used)
    if left:
        warnings.append(f"Детали без шага: {', '.join(sorted(left))}")
    tri = project.get("stats", {}).get("triangles", 0)
    if tri > TRI_BUDGET:
        warnings.append(f"Треугольников {tri} > бюджета {TRI_BUDGET}")
    return errors, warnings


def steps_json(project: dict) -> dict:
    keep = ("id", "title", "parts", "fasteners", "insertPath", "partPaths", "tool", "hintKey", "checks", "camera", "review")
    return {
        "formatVersion": FORMAT_VERSION,
        "parts": [{k: p[k] for k in ("id", "name", "qty", "tags")} for p in project["parts"]],
        "steps": [{k: st.get(k) for k in keep} for st in project["steps"]],
    }


def build_stark(project: dict, glb: bytes) -> bytes:
    manifest = {
        "formatVersion": FORMAT_VERSION,
        "productId": project["id"],
        "productName": project["name"],
        "author": project.get("author", ""),
        "createdAt": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "units": "m",
        "sourceUnits": "mm",
        "upAxis": "Y",
        "languages": sorted(k for k, v in project["hints"].items() if v),
        "generator": "stark-studio 0.1",
        "stats": project.get("stats", {}),
    }
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("manifest.json", json.dumps(manifest, ensure_ascii=False, indent=2))
        z.writestr("model.glb", glb)
        z.writestr("steps.json", json.dumps(steps_json(project), ensure_ascii=False, indent=2))
        for lang, hints in project["hints"].items():
            if hints:
                z.writestr(f"hints/{lang}.json", json.dumps(hints, ensure_ascii=False, indent=2))
    return buf.getvalue()


def read_stark(path: str | Path) -> dict:
    with zipfile.ZipFile(path) as z:
        out = {"manifest": json.loads(z.read("manifest.json")), "steps": json.loads(z.read("steps.json")), "hints": {}}
        for n in z.namelist():
            if n.startswith("hints/") and n.endswith(".json"):
                out["hints"][Path(n).stem] = json.loads(z.read(n))
        out["glb"] = z.read("model.glb")
    return out
