"""Проект Studio: конвейер STEP → дерево → сетки → план → шаги → подсказки, хранение на диске."""
from __future__ import annotations

import json
import os
import re
import shutil
import time
import uuid
from pathlib import Path

import numpy as np

from .cad_import import read_step
from .classify import classify
from .geometry import normalize, part_geoms, simplify_shapes
from .hints import LANGS, generate_hints
from .package import TRI_BUDGET, build_glb, build_stark, validate
from .planner import plan
from .steps import build_steps, renumber


def workspace() -> Path:
    p = Path(os.environ.get("STARK_WORKSPACE", Path.home() / ".stark-studio" / "projects"))
    p.mkdir(parents=True, exist_ok=True)
    return p


def _slug(name: str) -> str:
    s = re.sub(r"[^\w-]+", "-", name.lower(), flags=re.U).strip("-")
    return s[:40] or "project"


def project_dir(pid: str) -> Path:
    if not re.fullmatch(r"[\w-]+", pid):
        raise ValueError("bad project id")
    return workspace() / pid


def run_pipeline(step_path: Path, up_axis: str = "Z", tol: float = 0.15) -> tuple[dict, bytes]:
    """Полный конвейер. Возвращает (данные проекта без id, model.glb)."""
    t0 = time.time()
    asm = read_step(step_path)
    norm = normalize(asm, up_axis)
    geoms = part_geoms(asm, norm)
    tags = classify(asm.parts, geoms)
    names = {p.id: p.name for p in asm.parts}

    # дополнительные направления извлечения: оси самой детали
    local_dirs = {p.id: [norm[:3, :3] @ p.transform[:3, i] for i in range(3)] for p in asm.parts}
    t1 = time.time()
    pr = plan(geoms, tags, local_dirs=local_dirs, tol=tol)
    t2 = time.time()
    steps = build_steps(pr, names, tags)

    fastener_keys = {p.shape_key for p in asm.parts if "fastener" in tags[p.id]}
    meshes = simplify_shapes(asm, fastener_keys, TRI_BUDGET)
    glb, tri = build_glb(asm, norm, meshes)

    hints_ru, src = generate_hints(asm.name, steps, names, "ru", use_llm=False)
    parts = [{
        "id": p.id, "name": p.name, "qty": 1, "tags": tags[p.id], "shapeKey": p.shape_key,
        "color": [round(float(c), 3) for c in p.color], "path": p.path,
    } for p in asm.parts]
    data = {
        "name": asm.name,
        "upAxis": up_axis,
        "parts": parts,
        "steps": steps,
        "hints": {"ru": hints_ru, "kk": {}, "en": {}},
        "hintSource": {"ru": src},
        "stats": {
            "parts": len(parts),
            "uniqueShapes": len(asm.shapes),
            "steps": len(steps),
            "triangles": tri,
            "interferences": [list(x) for x in pr.interferences],
            "review": sum(1 for s in steps if s.get("review")),
            "timings": {"importSec": round(t1 - t0, 2), "planSec": round(t2 - t1, 2), "totalSec": round(time.time() - t0, 2)},
        },
    }
    return data, glb


def create_project(step_file: Path, original_name: str | None = None, up_axis: str = "Z") -> dict:
    pid = f"{_slug(Path(original_name or step_file.name).stem)}-{uuid.uuid4().hex[:6]}"
    d = project_dir(pid)
    d.mkdir(parents=True)
    src = d / "source.step"
    shutil.copy(step_file, src)
    try:
        data, glb = run_pipeline(src, up_axis)
    except Exception:
        shutil.rmtree(d, ignore_errors=True)
        raise
    data.update({"id": pid, "sourceFile": original_name or step_file.name,
                 "createdAt": time.strftime("%Y-%m-%dT%H:%M:%S")})
    (d / "model.glb").write_bytes(glb)
    save(data)
    return data


def replan(pid: str) -> dict:
    """Заново посчитать шаги (правки инженера в шагах теряются, подсказки по деталям — тоже)."""
    old = load(pid)
    data, glb = run_pipeline(project_dir(pid) / "source.step", old.get("upAxis", "Z"))
    for k in ("id", "sourceFile", "createdAt"):
        data[k] = old[k]
    (project_dir(pid) / "model.glb").write_bytes(glb)
    save(data)
    return data


def load(pid: str) -> dict:
    return json.loads((project_dir(pid) / "project.json").read_text("utf-8"))


def save(data: dict) -> None:
    p = project_dir(data["id"]) / "project.json"
    tmp = p.with_suffix(".tmp")
    tmp.write_text(json.dumps(data, ensure_ascii=False, indent=1), "utf-8")
    tmp.replace(p)


def list_projects() -> list[dict]:
    out = []
    for d in sorted(workspace().iterdir(), key=lambda x: x.stat().st_mtime, reverse=True):
        f = d / "project.json"
        if f.exists():
            j = json.loads(f.read_text("utf-8"))
            out.append({"id": j["id"], "name": j["name"], "sourceFile": j.get("sourceFile"),
                        "createdAt": j.get("createdAt"), "steps": len(j["steps"]), "parts": len(j["parts"])})
    return out


def update_steps(pid: str, steps: list[dict], hints: dict[str, dict[str, str]] | None) -> dict:
    data = load(pid)
    names = {p["id"]: p["name"] for p in data["parts"]}
    renumber(steps, names)
    data["steps"] = steps
    if hints is not None:
        for lang in LANGS:
            if lang in hints:
                data["hints"][lang] = {k: v for k, v in hints[lang].items() if isinstance(v, str)}
    data["stats"]["steps"] = len(steps)
    data["stats"]["review"] = sum(1 for s in steps if s.get("review"))
    save(data)
    return data


def regenerate_hints(pid: str, lang: str = "ru", only_missing: bool = False) -> tuple[dict, str]:
    data = load(pid)
    names = {p["id"]: p["name"] for p in data["parts"]}
    new, src = generate_hints(data["name"], data["steps"], names, lang)
    cur = data["hints"].setdefault(lang, {})
    for k, v in new.items():
        if not only_missing or not cur.get(k):
            cur[k] = v
    data.setdefault("hintSource", {})[lang] = src
    save(data)
    return data, src


def export(pid: str) -> tuple[bytes | None, list[str], list[str]]:
    data = load(pid)
    glb = (project_dir(pid) / "model.glb").read_bytes()
    errors, warnings = validate(data, glb)
    if errors:
        return None, errors, warnings
    return build_stark(data, glb), errors, warnings
