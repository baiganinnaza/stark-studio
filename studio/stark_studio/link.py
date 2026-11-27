"""Передача пакетов .stark на очки по локальной сети (Wi‑Fi).

Основной редактор слушает только 127.0.0.1 (NDA). Для очков поднимается отдельный
маленький сервер на всех интерфейсах (порт 8041), который отдаёт ровно одно:
пакет, «отправленный на очки» из редактора. Доступ по секретному токену, который
попадает в плеер через QR-код:

  QR → http://<ip>:8041/s/<token>   (страница для камеры Beam Pro / телефона)
     → кнопка «Открыть в Stark Player» = stark://link?u=http://<ip>:8041&t=<token>
     → плеер запоминает адрес и токен и раз в несколько секунд спрашивает
       GET /api/v1/current, при новой версии скачивает GET /api/v1/current.stark.

Протокол описан в spec/stark-link-v1.md.
"""
from __future__ import annotations

import hashlib
import hmac
import html
import json
import os
import secrets
import socket
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, quote, urlsplit

PROTOCOL = 1
DEFAULT_PORT = int(os.environ.get("STARK_LINK_PORT", 8041))
DEEP_LINK_SCHEME = "stark"


def _state_dir() -> Path:
    from .project import workspace
    p = Path(os.environ["STARK_LINK_DIR"]) if os.environ.get("STARK_LINK_DIR") else workspace().parent / "link"
    p.mkdir(parents=True, exist_ok=True)
    return p


def lan_addresses() -> list[str]:
    """IPv4-адреса этого компьютера в локальных сетях; первым идёт адрес маршрута по умолчанию."""
    found: list[str] = []
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
            s.connect(("10.255.255.255", 1))  # пакет не отправляется, только выбор интерфейса
            found.append(s.getsockname()[0])
    except OSError:
        pass
    try:
        for info in socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET):
            found.append(info[4][0])
    except OSError:
        pass
    out = []
    for ip in found:
        if ip not in out and not ip.startswith("127.") and not ip.startswith("169.254."):
            out.append(ip)
    return out


class LinkHub:
    """Состояние передачи: токен, текущий пакет, кто из очков заходил. Потокобезопасно."""

    def __init__(self, state_dir: Path | None = None):
        self._dir = state_dir
        self._lock = threading.Lock()
        self._server: ThreadingHTTPServer | None = None
        self._thread: threading.Thread | None = None
        self.port = DEFAULT_PORT
        self.host = "0.0.0.0"
        self.devices: dict[str, dict] = {}  # ip -> {name, lastSeen, version}
        self._token: str | None = None
        self._current: dict | None = None
        self._blob: bytes | None = None
        self._loaded = False

    # ---------- хранение ----------
    @property
    def dir(self) -> Path:
        return self._dir or _state_dir()

    def _load(self) -> None:
        if self._loaded:
            return
        self._loaded = True
        meta = self.dir / "link.json"
        if meta.exists():
            try:
                j = json.loads(meta.read_text("utf-8"))
                self._token = j.get("token")
                cur = j.get("current")
                blob_path = self.dir / "current.stark"
                if cur and blob_path.exists():
                    blob = blob_path.read_bytes()
                    if hashlib.sha256(blob).hexdigest() == cur.get("sha256"):
                        self._current, self._blob = cur, blob
            except (ValueError, OSError):
                pass
        if not self._token:
            self._token = secrets.token_urlsafe(18)
            self._save()

    def _save(self) -> None:
        meta = self.dir / "link.json"
        tmp = meta.with_suffix(".tmp")
        tmp.write_text(json.dumps({"token": self._token, "current": self._current}, ensure_ascii=False, indent=1), "utf-8")
        tmp.replace(meta)

    @property
    def token(self) -> str:
        with self._lock:
            self._load()
            return self._token

    def reset_token(self) -> str:
        """Новый токен: все ранее сопряжённые очки теряют доступ, нужно заново сканировать QR."""
        with self._lock:
            self._load()
            self._token = secrets.token_urlsafe(18)
            self.devices.clear()
            self._save()
            return self._token

    def check_token(self, token: str | None) -> bool:
        return bool(token) and hmac.compare_digest(token.encode(), self.token.encode())

    # ---------- пакет ----------
    def publish(self, blob: bytes, project_id: str, name: str, step_count: int) -> dict:
        with self._lock:
            self._load()
            version = (self._current or {}).get("version", 0) + 1
            cur = {
                "id": project_id,
                "name": name,
                "version": version,
                "sha256": hashlib.sha256(blob).hexdigest(),
                "size": len(blob),
                "steps": step_count,
                "sentAt": time.strftime("%Y-%m-%dT%H:%M:%S"),
            }
            path = self.dir / "current.stark"
            tmp = path.with_suffix(".tmp")
            tmp.write_bytes(blob)
            tmp.replace(path)
            self._current, self._blob = cur, blob
            self._save()
            return dict(cur)

    def current(self) -> tuple[dict | None, bytes | None]:
        with self._lock:
            self._load()
            return (dict(self._current) if self._current else None), self._blob

    def touch_device(self, ip: str, name: str | None, version: int | None = None) -> None:
        with self._lock:
            d = self.devices.setdefault(ip, {"name": name or ip})
            if name:
                d["name"] = name[:60]
            d["lastSeen"] = time.time()
            if version is not None:
                d["version"] = version

    # ---------- сервер ----------
    @property
    def running(self) -> bool:
        return self._server is not None

    def start(self, port: int | None = None, host: str | None = None) -> None:
        if self._server is not None:
            return
        self.port = self.port if port is None else port
        self.host = host or self.host
        self.token  # создать токен до первого запроса
        srv = ThreadingHTTPServer((self.host, self.port), _make_handler(self))
        srv.daemon_threads = True
        self.port = srv.server_address[1]  # если port=0 (тесты)
        self._server = srv
        self._thread = threading.Thread(target=srv.serve_forever, name="stark-link", daemon=True)
        self._thread.start()

    def stop(self) -> None:
        srv, self._server = self._server, None
        if srv is not None:
            srv.shutdown()
            srv.server_close()

    def base_urls(self) -> list[str]:
        # В Docker контейнер не знает адрес компьютера в сети: STARK_LINK_ADVERTISE=192.168.1.10
        adv = [h.strip() for h in os.environ.get("STARK_LINK_ADVERTISE", "").split(",") if h.strip()]
        hosts = adv or (lan_addresses() if self.host in ("0.0.0.0", "") else [self.host])
        return [f"http://{h}:{self.port}" for h in (hosts or ["127.0.0.1"])]

    def pair_url(self, base: str) -> str:
        return f"{base}/s/{self.token}"

    def deep_link(self, base: str) -> str:
        return f"{DEEP_LINK_SCHEME}://link?u={quote(base, safe='')}&t={quote(self.token, safe='')}"

    def status(self) -> dict:
        cur, _ = self.current()
        now = time.time()
        with self._lock:
            devices = [
                {"ip": ip, "name": d.get("name"), "secondsAgo": round(now - d.get("lastSeen", now)),
                 "version": d.get("version"), "online": now - d.get("lastSeen", 0) < 15}
                for ip, d in sorted(self.devices.items(), key=lambda kv: -kv[1].get("lastSeen", 0))
            ]
        urls = self.base_urls() if self.running else []
        return {
            "running": self.running,
            "port": self.port,
            "urls": urls,
            "pairUrls": [self.pair_url(u) for u in urls],
            "deepLinks": [self.deep_link(u) for u in urls],
            "current": cur,
            "devices": devices,
        }


hub = LinkHub()


def qr_svg(text: str) -> str:
    import segno
    import io
    buf = io.BytesIO()
    segno.make(text, error="m").save(buf, kind="svg", scale=6, border=2, dark="#111", light="#fff", xmldecl=False, svgns=True)
    return buf.getvalue().decode()


_PAGE = """<!doctype html><html lang="ru"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>Stark: подключение очков</title>
<style>body{{font:17px/1.45 system-ui,sans-serif;margin:0;padding:24px 16px;background:#f4f5f7;color:#16181d}}
main{{max-width:460px;margin:auto}}a.b{{display:block;text-align:center;padding:16px;border-radius:12px;background:#0e7490;color:#fff;
text-decoration:none;font-weight:600;margin:18px 0}}a.s{{display:block;text-align:center;color:#0e7490;margin-top:8px}}
.c{{background:#fff;border-radius:12px;padding:14px 16px;margin-top:12px}}small{{color:#5b616e}}</style></head><body><main>
<h2>Stark Studio → очки</h2>
<div class="c">{current}</div>
<a class="b" href="{deep}">Открыть в Stark Player</a>
<small>Кнопка запустит Stark Player и подключит его к этому компьютеру. После этого пакеты,
которые инженер нажимает «На очки», будут появляться сами.</small>
{download}
</main></body></html>"""


def _make_handler(hub_: LinkHub):
    class Handler(BaseHTTPRequestHandler):
        server_version = "StarkLink/1"

        def log_message(self, fmt, *args):  # не засорять консоль опросами очков
            pass

        def _token(self, qs: dict) -> str | None:
            return self.headers.get("X-Stark-Token") or (qs.get("t") or [None])[0]

        def _send(self, code: int, body: bytes, ctype: str, extra: dict | None = None):
            self.send_response(code)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            for k, v in (extra or {}).items():
                self.send_header(k, v)
            self.end_headers()
            if self.command != "HEAD":
                self.wfile.write(body)

        def _json(self, code: int, obj):
            self._send(code, json.dumps(obj, ensure_ascii=False).encode(), "application/json; charset=utf-8")

        def do_HEAD(self):
            self.do_GET()

        def do_GET(self):
            u = urlsplit(self.path)
            qs = parse_qs(u.query)
            ip = self.client_address[0]
            device = self.headers.get("X-Stark-Device")

            if u.path.startswith("/s/"):
                if not hub_.check_token(u.path[3:]):
                    return self._send(403, "Ссылка устарела: отсканируйте QR-код в Stark Studio заново".encode(), "text/plain; charset=utf-8")
                host = self.headers.get("Host") or f"{self.server.server_address[0]}:{hub_.port}"
                base = f"http://{host}"
                cur, _ = hub_.current()
                current = (f"Сейчас на очки отправлено: <b>{html.escape(cur['name'])}</b><br><small>версия {cur['version']}, "
                           f"шагов {cur['steps']}, {cur['sentAt'].replace('T', ' ')}</small>") if cur else \
                    "Пакет ещё не отправлен. В Studio нажмите «На очки»."
                download = (f'<a class="s" href="/api/v1/current.stark?t={quote(hub_.token)}">Скачать пакет .stark</a>' if cur else "")
                page = _PAGE.format(current=current, deep=html.escape(hub_.deep_link(base)), download=download)
                return self._send(200, page.encode(), "text/html; charset=utf-8")

            if u.path == "/api/v1/ping":
                return self._json(200, {"app": "stark-studio", "protocol": PROTOCOL})

            if u.path in ("/api/v1/current", "/api/v1/current.stark"):
                if not hub_.check_token(self._token(qs)):
                    return self._json(403, {"error": "bad_token", "message": "Очки не сопряжены: отсканируйте QR-код в Studio"})
                cur, blob = hub_.current()
                if u.path == "/api/v1/current":
                    hub_.touch_device(ip, device)
                    return self._json(200, {"app": "stark-studio", "protocol": PROTOCOL, "package": cur})
                if not cur:
                    return self._json(404, {"error": "no_package", "message": "В Studio ещё ничего не отправлено на очки"})
                etag = f'"{cur["sha256"]}"'
                if self.headers.get("If-None-Match") == etag:
                    return self._send(304, b"", "application/octet-stream", {"ETag": etag})
                hub_.touch_device(ip, device, cur["version"])
                fname = f'{cur["id"]}.stark'
                return self._send(200, blob, "application/zip", {
                    "ETag": etag,
                    "X-Stark-Version": str(cur["version"]),
                    "X-Stark-Sha256": cur["sha256"],
                    "Content-Disposition": f'attachment; filename="{fname}"',
                })

            self._json(404, {"error": "not_found"})

    return Handler
