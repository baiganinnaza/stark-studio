"""Передача на очки: токен, публикация пакета, LAN-сервер, API редактора."""
import hashlib
import json
import urllib.error
import urllib.request
from urllib.parse import parse_qs, unquote, urlsplit

import pytest
from fastapi.testclient import TestClient

from stark_studio import link as L
from stark_studio import project as P
from stark_studio.package import read_stark
from stark_studio.samples import write_sample_step


@pytest.fixture(scope="module")
def sample_step(tmp_path_factory):
    return write_sample_step(tmp_path_factory.mktemp("cad") / "gearbox.step")


@pytest.fixture()
def hub(tmp_path, monkeypatch):
    monkeypatch.setenv("STARK_LINK_ADVERTISE", "127.0.0.1")
    h = L.LinkHub(state_dir=tmp_path / "link")
    (tmp_path / "link").mkdir()
    h.start(port=0, host="127.0.0.1")
    yield h
    h.stop()


def get(url, headers=None):
    req = urllib.request.Request(url, headers=headers or {})
    try:
        with urllib.request.urlopen(req, timeout=5) as r:
            return r.status, dict(r.headers), r.read()
    except urllib.error.HTTPError as e:
        return e.code, dict(e.headers), e.read()


def test_token_is_required(hub):
    base = hub.base_urls()[0]
    assert get(f"{base}/api/v1/current")[0] == 403
    assert get(f"{base}/api/v1/current?t=wrong")[0] == 403
    assert get(f"{base}/api/v1/current.stark", {"X-Stark-Token": "wrong"})[0] == 403
    assert get(f"{base}/s/wrong")[0] == 403
    assert get(f"{base}/api/v1/ping")[0] == 200  # без секретов


def test_publish_and_download(hub):
    base, t = hub.base_urls()[0], hub.token
    code, _, body = get(f"{base}/api/v1/current?t={t}")
    assert code == 200 and json.loads(body)["package"] is None
    assert get(f"{base}/api/v1/current.stark?t={t}")[0] == 404

    blob = b"PK\x03\x04fake-stark"
    cur = hub.publish(blob, "gearbox-1", "Редуктор", 8)
    assert cur["version"] == 1 and cur["sha256"] == hashlib.sha256(blob).hexdigest()
    assert hub.publish(blob + b"2", "gearbox-1", "Редуктор", 8)["version"] == 2

    code, _, body = get(f"{base}/api/v1/current", {"X-Stark-Token": t, "X-Stark-Device": "Beam Pro"})
    pkg = json.loads(body)["package"]
    assert code == 200 and pkg["version"] == 2 and pkg["name"] == "Редуктор" and pkg["steps"] == 8

    code, headers, data = get(f"{base}/api/v1/current.stark", {"X-Stark-Token": t, "X-Stark-Device": "Beam Pro"})
    assert code == 200 and data == blob + b"2"
    assert headers["X-Stark-Sha256"] == pkg["sha256"] and headers["X-Stark-Version"] == "2"
    # повторная загрузка той же версии не гоняет байты
    assert get(f"{base}/api/v1/current.stark?t={t}", {"If-None-Match": headers["ETag"]})[0] == 304

    st = hub.status()
    dev = next(d for d in st["devices"] if d["name"] == "Beam Pro")
    assert dev["online"] and dev["version"] == 2


def test_pair_page_and_deep_link(hub):
    base, t = hub.base_urls()[0], hub.token
    code, _, page = get(hub.pair_url(base))
    assert code == 200
    page = page.decode()
    assert "Открыть в Stark Player" in page and "stark://link?" in page
    dl = hub.deep_link(base)
    q = parse_qs(urlsplit(dl).query)
    assert urlsplit(dl).scheme == "stark" and urlsplit(dl).netloc == "link"
    assert unquote(q["u"][0]) == base and q["t"][0] == t


def test_state_survives_restart_and_reset(hub, tmp_path):
    hub.publish(b"abc", "p1", "Изделие", 3)
    old = hub.token
    again = L.LinkHub(state_dir=tmp_path / "link")
    assert again.token == old
    cur, blob = again.current()
    assert cur["version"] == 1 and blob == b"abc"
    new = hub.reset_token()
    assert new != old and not hub.check_token(old) and hub.check_token(new)


def test_qr_svg_encodes_url():
    svg = L.qr_svg("http://192.168.1.10:8041/s/abc")
    assert svg.lstrip().startswith("<svg") and "path" in svg


def test_editor_send_endpoint(tmp_path, monkeypatch, sample_step):
    monkeypatch.setenv("STARK_WORKSPACE", str(tmp_path / "ws"))
    monkeypatch.setenv("STARK_LINK_ADVERTISE", "127.0.0.1")
    monkeypatch.delenv("ANTHROPIC_API_KEY", raising=False)
    from stark_studio import server
    h = L.LinkHub(state_dir=tmp_path / "link")
    (tmp_path / "link").mkdir()
    h.port = 0
    monkeypatch.setattr(L, "hub", h)
    try:
        c = TestClient(server.app)
        assert c.get("/api/link").json()["running"] is False
        assert c.get("/api/link/qr.svg").status_code == 409
        proj = P.create_project(sample_step, "gearbox.step")
        r = c.post(f"/api/projects/{proj['id']}/send")
        assert r.status_code == 200, r.text
        j = r.json()
        assert j["sent"]["version"] == 1 and j["link"]["running"] is True
        assert c.get("/api/link/qr.svg").headers["content-type"].startswith("image/svg")
        # очки скачивают ровно этот пакет
        base = j["link"]["urls"][0]
        code, _, data = get(f"{base}/api/v1/current.stark?t={h.token}")
        assert code == 200
        pkg_path = tmp_path / "got.stark"
        pkg_path.write_bytes(data)
        assert read_stark(pkg_path)["manifest"]["productId"] == proj["id"]
        assert c.post("/api/link/stop").json()["running"] is False
    finally:
        h.stop()
