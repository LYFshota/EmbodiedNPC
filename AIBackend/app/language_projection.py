"""Versioned model-only D projection; never deserialize this view as source DTOs.

Default translations apply only on an exact semantic source hash match. Overrides
and newer assets retain their own text. Budgeting sees the actual rendered view.
"""
from functools import lru_cache
from pathlib import Path
import hashlib
import json

_ROOT = Path(__file__).with_name("language")


def semantic_hash(value: dict) -> str:
    return hashlib.sha256(json.dumps(value, ensure_ascii=False, sort_keys=True,
                                    separators=(",", ":")).encode("utf-8")).hexdigest()


@lru_cache(maxsize=1)
def _resources():
    personas = json.loads((_ROOT / "default_personas.v1.json").read_text(encoding="utf-8"))
    behavior = json.loads((_ROOT / "behavior.v1.json").read_text(encoding="utf-8"))
    return personas, behavior


def english_character(character: dict) -> dict:
    entry = _resources()[0]["entries"].get(character["character_id"])
    if (entry is not None and semantic_hash(character) == entry["source_sha256"]
            and semantic_hash(entry["english"]) == entry["projection_sha256"]):
        return entry["english"]
    return character


def is_default_english_projection(character: dict) -> bool:
    entry = _resources()[0]["entries"].get(character.get("character_id"))
    return entry is not None and semantic_hash(character) == entry["projection_sha256"]


_CONTINUATION_REVIEW = (
    "Final continuation check: this is an autonomous NPC opportunity with no new player speech. "
    "Read all already displayed NPC utterances in the last PRIOR DIALOGUE turn. If a question, "
    "choice, farewell or refusal to continue is pending, choose NO_ACTION; never answer for the "
    "player, repeat a question or urge an answer. Without distinct grounded content, also choose "
    "NO_ACTION. Thanking the player for a pause, paraphrasing or asking generically what to discuss "
    "is not new content. An open topic may support a distinct personal view, specific reason or "
    "relevant follow-up; questions are optional. Warmth is not salesmanship; reserve permits silence. "
    "Interests and opinions may be expressed naturally, and reasonable personal background may "
    "develop in unspecified Persona areas. Retain known self-statements; invent no player, other "
    "person or shared experiences. Personal stories prove neither external facts nor completed "
    "actions. Do not add an anecdote solely to continue. NO_ACTION needs no explanation."
)


def project_text(system: str, user: str, language: str) -> tuple[str, str]:
    if language != "en-US":
        return system, user
    # Prose only: historical quotes, knowledge, perception and event text remain exact.
    mappings = _resources()[1]
    for source, target in mappings.items():
        system = system.replace(source, target)
    for zh, en in (("凌晨", "early morning"), ("上午", "morning"),
                   ("下午", "afternoon"), ("晚上", "evening")):
        system = system.replace("daypart=" + zh, "daypart=" + en)
    lines = user.splitlines()
    for i, line in enumerate(lines):
        if line.startswith("Cognitive context JSON: "):
            context = json.loads(line.split(": ", 1)[1])
            context["character"] = english_character(context["character"])
            lines[i] = "Cognitive context JSON: " + json.dumps(context, ensure_ascii=False, separators=(",", ":"))
        elif line.startswith("续话最终检查："):
            lines[i] = _CONTINUATION_REVIEW
        elif line.startswith(("空间身份：", "正常回复最终检查：", "表达检查：")):
            for source, target in mappings.items():
                line = line.replace(source, target)
            lines[i] = line
    return system, "\n".join(lines)
