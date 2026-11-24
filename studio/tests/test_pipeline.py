import io
import json
import zipfile
from pathlib import Path

import pytest

from stark_studio import project as P
from stark_studio.package import glb_node_names
from stark_studio.samples import write_sample_step


@pytest.fixture(scope="session")
def sample_step(tmp_path_factory):
    return write_sample_step(tmp_path_factory.mktemp("cad") / "gearbox.step")


@pytest.fixture()
def ws(tmp_path, monkeypatch):
    monkeypatch.setenv("STARK_WORKSPACE", str(tmp_path / "ws"))
    monkeypatch.delenv("ANTHROPIC_API_KEY", raising=False)
    return tmp_path


@pytest.fixture()
def proj(ws, sample_step):
    return P.create_project(sample_step, "gearbox.step")


def order(proj):
    n = {p["id"]: p["name"] for p in proj["parts"]}
    return [n[s["parts"][0]].split(":")[0] for s in proj["steps"]]


def test_import_tree(proj):
    names = [p["name"] for p in proj["parts"]]
    assert len(names) == 17
    assert "Основание" in names and "Крышка корпуса" in names  # кириллица из STEP читается
    assert proj["stats"]["uniqueShapes"] == 10                  # болты = экземпляры одной сетки
    fast = [p for p in proj["parts"] if "fastener" in p["tags"]]
    assert len(fast) == 8


def test_assembly_order_is_physical(proj):
    o = order(proj)
    assert o[0] == "Основание"
    assert o.index("Подшипник 6201") < o.index("Вал") < o.index("Шестерня Z40")
    assert o.index("Шестерня Z40") < o.index("Корпус") < o.index("Крышка корпуса") < o.index("Маховик")
    assert o.index("Корпус") < o.index("Кронштейн")
    assert proj["stats"]["interferences"] == []


def test_fasteners_attached_to_their_part(proj):
    n = {p["id"]: p["name"] for p in proj["parts"]}
    by_main = {n[s["parts"][0]].split(":")[0]: s for s in proj["steps"]}
    cover = by_main["Крышка корпуса"]
    assert [f["qty"] for f in cover["fasteners"]] == [4] and "M6" in cover["fasteners"][0]["name"]
    bracket = by_main["Кронштейн"]
    assert len(bracket["parts"]) == 2 and bracket["fasteners"][0]["qty"] == 4
    # болты M4 ставятся горизонтально (по ±X), а не сверху
    for path in bracket["fasteners"][0]["partPaths"].values():
        start = path[0]
        assert abs(start[0]) > 0.03 and abs(start[1]) < 1e-6


def test_insert_paths_point_up_for_vertical_parts(proj):
    for s in proj["steps"]:
        assert s["insertPath"][1] == [0.0, 0.0, 0.0]
    housing = next(s for s in proj["steps"] if "Корпус" in s["title"])
    assert housing["insertPath"][0][1] > 0.04  # glTF Y вверх, метры


def test_export_stark(proj):
    blob, errors, warnings = P.export(proj["id"])
    assert errors == []
    z = zipfile.ZipFile(io.BytesIO(blob))
    assert {"manifest.json", "model.glb", "steps.json", "hints/ru.json"} <= set(z.namelist())
    steps = json.loads(z.read("steps.json"))
    assert steps["formatVersion"] == 1
    nodes = glb_node_names(z.read("model.glb"))
    assert {p["id"] for p in steps["parts"]} <= nodes
    hints = json.loads(z.read("hints/ru.json"))
    assert all(len(hints[s["hintKey"]]) <= 80 for s in steps["steps"])


def test_edit_reorder_keeps_hints(proj):
    steps = proj["steps"]
    hints = proj["hints"]
    last_key = steps[-1]["hintKey"]
    hints["ru"][last_key] = "Наденьте маховик на вал"
    steps = [steps[-1]] + steps[:-1]
    data = P.update_steps(proj["id"], steps, hints)
    assert data["steps"][0]["id"] == "S01" and data["steps"][0]["hintKey"] == last_key
    assert data["hints"]["ru"][last_key] == "Наденьте маховик на вал"


def test_validation_blocks_missing_hint(proj):
    hints = proj["hints"]
    hints["ru"][proj["steps"][0]["hintKey"]] = ""
    P.update_steps(proj["id"], proj["steps"], hints)
    blob, errors, _ = P.export(proj["id"])
    assert blob is None and any("подсказки" in e for e in errors)


def test_http_api(ws, sample_step):
    from fastapi.testclient import TestClient
    from stark_studio.server import app
    c = TestClient(app)
    with open(sample_step, "rb") as f:
        r = c.post("/api/projects", files={"file": ("gearbox.step", f, "application/octet-stream")})
    assert r.status_code == 200, r.text
    pid = r.json()["id"]
    assert c.get(f"/api/projects/{pid}/model.glb").status_code == 200
    r = c.post(f"/api/projects/{pid}/hints", json={"lang": "kk"})
    assert r.json()["source"] == "template"
    r = c.get(f"/api/projects/{pid}/export")
    assert r.status_code == 200 and r.content[:2] == b"PK"
    assert c.get("/").status_code == 200
    assert c.get("/api/projects/../etc").status_code == 404
