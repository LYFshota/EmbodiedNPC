"""One authored Cafe menu shared with Unity; no inventory or persistent state."""
import json
from pathlib import Path
from types import MappingProxyType
from typing import Literal

# A standalone bundle may copy the same source resource beside this module.
_source = Path(__file__).with_name("knowledge") / "cafe_snacks.json"
if not _source.is_file():
    _source = Path(__file__).resolve().parents[2] / "UnityClient/Assets/Resources/CafeSnackCatalog.json"
_document = json.loads(_source.read_text(encoding="utf-8-sig"))
SNACKS = tuple(MappingProxyType(item) for item in _document["items"])
SNACK_TYPE_IDS = tuple(item["id"] for item in SNACKS)
SNACK_QUANTITY_IDS = tuple(_document["quantity_ids"])
SnackType = Literal[*SNACK_TYPE_IDS]
SnackQuantity = Literal[*SNACK_QUANTITY_IDS]


def menu_guidance() -> str:
    menu = "; ".join(f'{x["id"]}={x["chinese"]}/{x["english"]}: {x["description"]}' for x in SNACKS)
    return (
        f"Configured Cafe snack menu: {menu}. Only these snacks can be offered or recommended. "
        f"One immediate order serves one snack type with quantity as an exact string enum {SNACK_QUANTITY_IDS}; "
        "ask clarification for unsupported types, unspecified types or out-of-range quantities. "
        "Menu questions and recommendations are DIRECT_RESPONSE, not service requests. "
        "Use an immediate serve_snack only for an explicit current player order. Future orders use an "
        "advertised typed task; a trusted offered task opportunity may later execute its exact snack order "
        "through the existing reservation/start protocol. This is a logical Demo service, "
        "not a physical entity handover or inventory. Menu/display models do not prove stock counts, "
        "prices, ingredients or flavours. Do not promise those. Acceptance is not completion; "
        "only Unity's successful execution and service receipt confirm provision."
    )
