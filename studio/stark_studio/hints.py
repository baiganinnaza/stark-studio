"""Короткие подсказки к шагам (≤80 символов): LLM, если есть ключ API, иначе шаблон.

В LLM уходят только названия изделия и деталей, инструмент и крепёж. Геометрия и
сам CAD-файл компьютер не покидают (NDA).
"""
from __future__ import annotations

import json
import os
import re

from .steps import base_name

MAX_LEN = 80
LANGS = ("ru", "kk", "en")
DEFAULT_MODEL = os.environ.get("STARK_LLM_MODEL", "claude-opus-5-5")


def template_hint(step: dict, names: dict[str, str], lang: str = "ru") -> str:
    n = base_name(names.get(step["parts"][0], ""))
    qty = len(step["parts"])
    q = f" ×{qty}" if qty > 1 else ""
    fast = step.get("fasteners", [])
    if lang == "en":
        t = f"Place: {n}{q}"
        if fast:
            t += "; fasten " + ", ".join(f"{f['name']} ×{f['qty']}" for f in fast)
    elif lang == "kk":
        t = f"Орнатыңыз: {n}{q}"
        if fast:
            t += "; бекітіңіз: " + ", ".join(f"{f['name']} ×{f['qty']}" for f in fast)
    else:
        t = f"Установите: {n}{q}"
        if fast:
            t += "; закрепите " + ", ".join(f"{f['name']} ×{f['qty']}" for f in fast)
    if step.get("tool"):
        t += f" ({step['tool']})"
    return t[:MAX_LEN]


def _llm_payload(product: str, steps: list[dict], names: dict[str, str]) -> list[dict]:
    out = []
    for st in steps:
        out.append({
            "key": st["hintKey"],
            "parts": [names.get(p, p) for p in st["parts"]],
            "fasteners": [{"name": f["name"], "qty": f["qty"], "torqueNm": f.get("torqueNm")} for f in st.get("fasteners", [])],
            "tool": st.get("tool") or None,
            "checks": st.get("checks") or [],
        })
    return out


LANG_NAMES = {"ru": "русском", "kk": "казахском", "en": "английском"}


def llm_hints(product: str, steps: list[dict], names: dict[str, str], lang: str = "ru") -> dict[str, str]:
    """Сгенерировать подсказки через Claude API. Бросает исключение, если API недоступен."""
    import anthropic

    client = anthropic.Anthropic()
    payload = _llm_payload(product, steps, names)
    prompt = (
        f"Изделие: {product}.\n"
        f"Ниже шаги сборки в JSON. Для каждого шага напиши подсказку сборщику на {LANG_NAMES.get(lang, 'русском')} языке, "
        f"которую он прочитает в AR-очках во время работы: 1 короткое предложение, повелительное наклонение, "
        f"не длиннее {MAX_LEN} символов, без кавычек и нумерации. Упомяни крепёж с количеством и момент затяжки, если он указан. "
        "Не выдумывай инструмент и размеры, которых нет в данных.\n"
        'Ответ: только JSON-объект вида {"<key>": "<подсказка>"} для каждого key.\n\n'
        + json.dumps(payload, ensure_ascii=False)
    )
    kwargs = dict(
        model=DEFAULT_MODEL,
        max_tokens=8000,
        output_config={"effort": "low"},
        messages=[{"role": "user", "content": prompt}],
    )
    try:
        resp = client.beta.messages.create(betas=["server-side-fallback-2026-07-01"], fallbacks="default", **kwargs)
    except (TypeError, anthropic.BadRequestError):
        resp = client.messages.create(**kwargs)
    if resp.stop_reason == "refusal":
        raise RuntimeError("Модель отказалась отвечать")
    text = "".join(b.text for b in resp.content if getattr(b, "type", "") == "text")
    m = re.search(r"\{.*\}", text, re.S)
    if not m:
        raise RuntimeError("LLM вернул ответ без JSON")
    data = json.loads(m.group(0))
    return {k: str(v).strip()[:MAX_LEN] for k, v in data.items() if isinstance(v, str)}


def generate_hints(product: str, steps: list[dict], names: dict[str, str], lang: str = "ru",
                   use_llm: bool | None = None) -> tuple[dict[str, str], str]:
    """Возвращает (подсказки, источник: 'llm' | 'template')."""
    if use_llm is None:
        use_llm = bool(os.environ.get("ANTHROPIC_API_KEY") or os.environ.get("ANTHROPIC_AUTH_TOKEN"))
    hints = {st["hintKey"]: template_hint(st, names, lang) for st in steps}
    if use_llm:
        try:
            got = llm_hints(product, steps, names, lang)
            hints.update({k: v for k, v in got.items() if k in hints and v})
            return hints, "llm"
        except Exception as e:  # noqa: BLE001 - без сети работаем на шаблонах
            return hints, f"template (LLM недоступен: {type(e).__name__})"
    return hints, "template"
