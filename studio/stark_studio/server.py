"""HTTP API + веб-редактор. Запуск: stark-studio serve (по умолчанию только localhost)."""
from __future__ import annotations

import tempfile
from pathlib import Path

from fastapi import FastAPI, File, Form, HTTPException, UploadFile
from fastapi.responses import FileResponse, JSONResponse, Response
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel

from . import link as L
from . import project as P
from .samples import write_sample_step

EDITOR_DIR = Path(__file__).parent / "editor"
MAX_UPLOAD = 300 * 1024 * 1024

app = FastAPI(title="Stark Studio", version="0.1")


def _get(pid: str) -> dict:
    try:
        return P.load(pid)
    except (FileNotFoundError, ValueError):
        raise HTTPException(404, "Проект не найден")


@app.get("/api/projects")
def projects():
    return P.list_projects()


@app.post("/api/projects")
async def upload(file: UploadFile = File(...), up_axis: str = Form("Z")):
    if not file.filename.lower().endswith((".step", ".stp")):
        raise HTTPException(400, "Нужен файл .step или .stp")
    if up_axis.upper() not in ("Z", "Y"):
        raise HTTPException(400, "up_axis: Z или Y")
    with tempfile.NamedTemporaryFile(suffix=".step", delete=False) as tmp:
        size = 0
        while chunk := await file.read(1 << 20):
            size += len(chunk)
            if size > MAX_UPLOAD:
                raise HTTPException(413, "Файл больше 300 МБ")
            tmp.write(chunk)
    try:
        data = P.create_project(Path(tmp.name), file.filename, up_axis.upper())
    except ValueError as e:
        raise HTTPException(422, str(e))
    finally:
        Path(tmp.name).unlink(missing_ok=True)
    return data


@app.post("/api/samples/gearbox")
def sample():
    with tempfile.TemporaryDirectory() as d:
        f = write_sample_step(Path(d) / "gearbox_module.step")
        return P.create_project(f, "gearbox_module.step")


@app.get("/api/projects/{pid}")
def get_project(pid: str):
    return _get(pid)


@app.get("/api/projects/{pid}/model.glb")
def model(pid: str):
    _get(pid)
    return FileResponse(P.project_dir(pid) / "model.glb", media_type="model/gltf-binary")


class StepsUpdate(BaseModel):
    steps: list[dict]
    hints: dict[str, dict[str, str]] | None = None


@app.put("/api/projects/{pid}/steps")
def put_steps(pid: str, body: StepsUpdate):
    _get(pid)
    return P.update_steps(pid, body.steps, body.hints)


@app.post("/api/projects/{pid}/replan")
def replan(pid: str):
    _get(pid)
    return P.replan(pid)


class HintsRequest(BaseModel):
    lang: str = "ru"
    only_missing: bool = False


@app.post("/api/projects/{pid}/hints")
def hints(pid: str, body: HintsRequest):
    _get(pid)
    if body.lang not in ("ru", "kk", "en"):
        raise HTTPException(400, "lang: ru, kk или en")
    data, src = P.regenerate_hints(pid, body.lang, body.only_missing)
    return {"project": data, "source": src}


@app.get("/api/projects/{pid}/validate")
def validate(pid: str):
    _get(pid)
    _, errors, warnings = P.export(pid)
    return {"errors": errors, "warnings": warnings}


@app.get("/api/projects/{pid}/export")
def export(pid: str):
    data = _get(pid)
    blob, errors, warnings = P.export(pid)
    if blob is None:
        return JSONResponse({"errors": errors, "warnings": warnings}, status_code=422)
    fname = f"{pid}.stark"
    return Response(blob, media_type="application/zip",
                    headers={"Content-Disposition": f'attachment; filename="{fname}"', "X-Stark-Warnings": str(len(warnings))})


# ---------- передача на очки (Wi‑Fi + QR) ----------

class LinkStart(BaseModel):
    port: int | None = None


@app.get("/api/link")
def link_status():
    return L.hub.status()


@app.post("/api/link/start")
def link_start(body: LinkStart | None = None):
    try:
        L.hub.start(port=body.port if body else None)
    except OSError as e:
        raise HTTPException(409, f"Порт {L.hub.port} занят или недоступен: {e.strerror or e}")
    return L.hub.status()


@app.post("/api/link/stop")
def link_stop():
    L.hub.stop()
    return L.hub.status()


@app.post("/api/link/reset")
def link_reset():
    L.hub.reset_token()
    return L.hub.status()


@app.get("/api/link/qr.svg")
def link_qr(i: int = 0):
    st = L.hub.status()
    if not st["running"]:
        raise HTTPException(409, "Передача на очки выключена")
    urls = st["pairUrls"]
    return Response(L.qr_svg(urls[min(max(i, 0), len(urls) - 1)]), media_type="image/svg+xml",
                    headers={"Cache-Control": "no-store"})


@app.post("/api/projects/{pid}/send")
def send_to_glasses(pid: str):
    """Собрать .stark и сделать его текущим пакетом для очков. Очки подхватят его сами."""
    data = _get(pid)
    blob, errors, warnings = P.export(pid)
    if blob is None:
        return JSONResponse({"errors": errors, "warnings": warnings}, status_code=422)
    if not L.hub.running:
        try:
            L.hub.start()
        except OSError as e:
            raise HTTPException(409, f"Не удалось включить передачу: {e.strerror or e}")
    cur = L.hub.publish(blob, pid, data["name"], len(data["steps"]))
    return {"sent": cur, "warnings": warnings, "link": L.hub.status()}


app.mount("/", StaticFiles(directory=EDITOR_DIR, html=True), name="editor")
