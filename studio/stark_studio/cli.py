"""Командная строка: stark-studio serve | process | sample."""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path


def main(argv=None):
    ap = argparse.ArgumentParser(prog="stark-studio", description="CAD (STEP) → шаги сборки → пакет .stark")
    sub = ap.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("serve", help="запустить веб-редактор")
    s.add_argument("--host", default="127.0.0.1", help="по умолчанию только этот компьютер (NDA)")
    s.add_argument("--port", type=int, default=8040)
    s.add_argument("--link", action="store_true", help="сразу включить передачу на очки по Wi‑Fi (порт 8041)")
    p = sub.add_parser("process", help="STEP → .stark без редактора")
    p.add_argument("step")
    p.add_argument("-o", "--out")
    p.add_argument("--up", default="Z", choices=["Z", "Y"])
    p.add_argument("--llm", action="store_true", help="подсказки через Claude API (нужен ANTHROPIC_API_KEY)")
    g = sub.add_parser("sample", help="записать тестовую STEP-сборку")
    g.add_argument("out", nargs="?", default="gearbox_module.step")
    a = ap.parse_args(argv)

    if a.cmd == "serve":
        import uvicorn
        print(f"Stark Studio: http://{a.host}:{a.port}")
        if a.link:
            from .link import hub
            hub.start()
            for u in hub.base_urls():
                print(f"Передача на очки: {hub.pair_url(u)}")
        uvicorn.run("stark_studio.server:app", host=a.host, port=a.port)
    elif a.cmd == "sample":
        from .samples import write_sample_step
        print(write_sample_step(a.out))
    elif a.cmd == "process":
        from . import project as P
        data = P.create_project(Path(a.step), Path(a.step).name, a.up)
        if a.llm:
            data, src = P.regenerate_hints(data["id"], "ru")
            print("подсказки:", src, file=sys.stderr)
        blob, errors, warnings = P.export(data["id"])
        for w in warnings:
            print("предупреждение:", w, file=sys.stderr)
        if errors:
            print("\n".join(errors), file=sys.stderr)
            sys.exit(1)
        out = Path(a.out or Path(a.step).with_suffix(".stark").name)
        out.write_bytes(blob)
        print(json.dumps({"out": str(out), **{k: data["stats"][k] for k in ("parts", "steps", "triangles", "review")}}, ensure_ascii=False))


if __name__ == "__main__":
    main()
