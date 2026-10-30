"""K0 contracts and bounded retrieval, independent of personal Memory and world authority."""

from __future__ import annotations

import json
import re
from datetime import datetime
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field, JsonValue, field_validator, model_validator

from .context_token_estimator import ContextTokenEstimator
from .contracts import CognitiveRequest


def compact(value: dict) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"), allow_nan=False)


class KnowledgeModel(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)


class KnowledgeAccess(KnowledgeModel):
    mode: Literal["public", "restricted"] = "restricted"
    character_ids: tuple[str, ...] = Field(default=(), max_length=32)
    deployment_ids: tuple[str, ...] = Field(min_length=1, max_length=16)
    scene_ids: tuple[str, ...] = Field(default=(), max_length=16)

    @field_validator("character_ids", "deployment_ids", "scene_ids")
    @classmethod
    def validate_scopes(cls, values):
        if len(set(values)) != len(values) or any(not v.strip() or len(v) > 120 for v in values):
            raise ValueError("KNOWLEDGE_SCOPE_INVALID")
        return values

    @model_validator(mode="after")
    def explicit_character_scope(self):
        if self.mode == "restricted" and not self.character_ids:
            raise ValueError("KNOWLEDGE_RESTRICTED_CHARACTERS_REQUIRED")
        if self.mode == "public" and self.character_ids:
            raise ValueError("KNOWLEDGE_PUBLIC_CHARACTERS_MUST_BE_EMPTY")
        return self


class KnowledgeEntry(KnowledgeModel):
    knowledge_id: str = Field(pattern=r"^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,119}$")
    name: str = Field(min_length=1, max_length=160)
    content: str = Field(min_length=1, max_length=2000)
    knowledge_type: str = Field(min_length=1, max_length=80)
    attributes: dict[str, JsonValue] = Field(default_factory=dict, max_length=32)
    keywords: tuple[str, ...] = Field(default=(), max_length=32)
    related_ids: tuple[str, ...] = Field(default=(), max_length=16)
    source: str = Field(min_length=1, max_length=240)
    version: int = Field(default=1, ge=1, strict=True)
    updated_at: datetime
    access: KnowledgeAccess
    usage: Literal["constant", "retrieval"] = "retrieval"

    @field_validator("name", "content", "knowledge_type", "source")
    @classmethod
    def nonblank(cls, value):
        if not value.strip():
            raise ValueError("KNOWLEDGE_TEXT_EMPTY")
        return value

    @field_validator("updated_at")
    @classmethod
    def aware_time(cls, value):
        if value.tzinfo is None:
            raise ValueError("KNOWLEDGE_TIME_REQUIRES_TIMEZONE")
        return value

    @field_validator("attributes")
    @classmethod
    def bounded_attributes(cls, value):
        if len(compact(value)) > 2000 or any(not k.strip() or len(k) > 80 for k in value):
            raise ValueError("KNOWLEDGE_ATTRIBUTES_TOO_LARGE")
        return value

    @field_validator("keywords", "related_ids")
    @classmethod
    def bounded_terms(cls, value):
        if len(set(value)) != len(value) or any(not v.strip() or len(v) > 120 for v in value):
            raise ValueError("KNOWLEDGE_TERMS_INVALID")
        return value


class KnowledgeBundle(KnowledgeModel):
    schema_version: Literal[1] = 1
    bundle_version: str = Field(min_length=1, max_length=80)
    entries: tuple[KnowledgeEntry, ...]

    @model_validator(mode="after")
    def unique_ids(self):
        ids = [e.knowledge_id for e in self.entries]
        if len(ids) != len(set(ids)):
            raise ValueError("KNOWLEDGE_ID_DUPLICATE")
        return self


class KnowledgeBinding(KnowledgeModel):
    agent_entity_id: str = Field(min_length=1, max_length=120)
    character_id: str = Field(min_length=1, max_length=120)
    identity_namespace: str = Field(min_length=1, max_length=80)
    identity_type: str = Field(min_length=1, max_length=80)
    persistent_id: str = Field(min_length=1, max_length=160)


class KnowledgePrincipal(KnowledgeModel):
    """Constructed by the Backend deployment resolver; never from an LLM query."""
    character_id: str
    deployment_id: str
    scene_id: str | None = None


class KnowledgeDeployment(KnowledgeModel):
    schema_version: Literal[1] = 1
    deployment_id: str = Field(min_length=1, max_length=120)
    scene_id: str | None = Field(default=None, min_length=1, max_length=120)
    bindings: tuple[KnowledgeBinding, ...] = Field(max_length=128)

    @model_validator(mode="after")
    def unique_agents(self):
        ids = [b.agent_entity_id for b in self.bindings]
        identities = [(b.identity_namespace, b.identity_type, b.persistent_id) for b in self.bindings]
        if len(ids) != len(set(ids)) or len(identities) != len(set(identities)):
            raise ValueError("KNOWLEDGE_BINDING_DUPLICATE")
        return self

    def resolve(self, request: CognitiveRequest) -> KnowledgePrincipal | None:
        binding = next((b for b in self.bindings if b.agent_entity_id == request.run.agent_entity_id), None)
        wire = request.recall_identity
        if binding is None or wire is None:
            return None
        owner = wire.observer
        if (owner.entity_id != binding.agent_entity_id or owner.kind != "npc"
                or (owner.identity_namespace, owner.identity_type, owner.persistent_id) !=
                (binding.identity_namespace, binding.identity_type, binding.persistent_id)
                or request.context.character.character_id != binding.character_id):
            return None
        return KnowledgePrincipal(character_id=binding.character_id,
                                  deployment_id=self.deployment_id, scene_id=self.scene_id)


class KnowledgeQuery(KnowledgeModel):
    knowledge_ids: tuple[str, ...] = Field(default=(), max_length=16)
    knowledge_type: str | None = Field(default=None, min_length=1, max_length=80)
    attributes: dict[str, JsonValue] = Field(default_factory=dict, max_length=16)
    text: str = Field(default="", max_length=512)
    top_k: int = Field(default=3, ge=1, le=8, strict=True)
    token_budget: int = Field(default=1000, ge=0, le=2000, strict=True)

    @field_validator("attributes")
    @classmethod
    def bounded_attributes(cls, value):
        return KnowledgeEntry.bounded_attributes(value)


class KnowledgeContext(KnowledgeModel):
    status: Literal["OK", "NO_MATCH", "UNAUTHORIZED", "UNAVAILABLE", "BUDGET_EXHAUSTED", "SNAPSHOT_CHANGED"]
    bundle_version: str | None = None
    revision: int | None = None
    supplement_status: Literal["OK", "NO_MATCH", "UNAUTHORIZED", "UNAVAILABLE", "BUDGET_EXHAUSTED", "SNAPSHOT_CHANGED"] | None = None
    items: tuple[dict[str, JsonValue], ...] = ()
    constant_items: tuple[dict[str, JsonValue], ...] = ()
    truncated: bool = False
    developer_defined: Literal[True] = True
    not_personal_memory: Literal[True] = True
    not_current_world_state: Literal[True] = True
    not_action_authorization: Literal[True] = True

    def serialized(self) -> str:
        return compact(self.model_dump(mode="json"))


def permitted(entry: KnowledgeEntry, principal: KnowledgePrincipal) -> bool:
    access = entry.access
    return ((access.mode == "public" or principal.character_id in access.character_ids)
            and principal.deployment_id in access.deployment_ids
            and (not access.scene_ids or principal.scene_id in access.scene_ids))


def text_score(entry: KnowledgeEntry, text: str) -> int:
    """Literal IDs/aliases and lexical overlap only; no intent or authority rules."""
    query = text.casefold()
    if not query.strip():
        return 0
    aliases = (entry.knowledge_id, entry.name, *entry.keywords)
    score = sum(4 for alias in aliases if alias.casefold() in query)
    haystack = f"{entry.name} {entry.content}".casefold()
    terms = set(re.findall(r"[a-z0-9_]+|[\u4e00-\u9fff]+", query))
    # Chinese has no spaces: use literal bigrams, without semantic classification.
    for term in terms:
        if term.isascii():
            score += int(len(term) >= 2 and term in haystack)
        else:
            score += sum(1 for gram in {term[i:i+2] for i in range(len(term)-1)} if gram in haystack)
    return score


def knowledge_item(entry: KnowledgeEntry, allowed_ids: set[str]) -> dict:
    item = entry.model_dump(mode="json", exclude={"access", "keywords"})
    item["related_ids"] = [ref for ref in entry.related_ids if ref in allowed_ids]
    return item


def constant_knowledge(entries, principal) -> tuple[dict, ...]:
    allowed = tuple(e for e in entries if permitted(e, principal))
    allowed_ids = {e.knowledge_id for e in allowed}
    return tuple(knowledge_item(e, allowed_ids) for e in allowed if e.usage == "constant")


def retrieve_knowledge(entries: tuple[KnowledgeEntry, ...], principal: KnowledgePrincipal,
                       query: KnowledgeQuery, *, bundle_version: str, revision: int,
                       ranker=None, defer_budget: bool = False) -> KnowledgeContext:
    """Access before ranking/Top-K/budget. Exact IDs and filters compose by AND."""
    allowed = tuple(e for e in entries if permitted(e, principal))
    allowed_ids = {e.knowledge_id for e in allowed}
    eligible = []
    for entry in allowed:
        if entry.usage != "retrieval":
            continue
        if query.knowledge_ids and entry.knowledge_id not in query.knowledge_ids:
            continue
        if query.knowledge_type and entry.knowledge_type != query.knowledge_type:
            continue
        if any(k not in entry.attributes or entry.attributes[k] != v for k, v in query.attributes.items()):
            continue
        eligible.append(entry)
    if not (query.text.strip() or query.knowledge_ids or query.attributes or query.knowledge_type):
        ranked = ()
    elif ranker is not None:
        # Ranker receives only already-authorized, structurally matching IDs.
        ranked = ranker.rank(tuple(eligible), query, bundle_version=bundle_version)
    else:
        ranked = lexical_rank(tuple(eligible), query)
    result = KnowledgeContext(status="NO_MATCH", bundle_version=bundle_version, revision=revision)
    estimator = ContextTokenEstimator()
    budget_excluded = False
    for entry in ranked:
        item = knowledge_item(entry, allowed_ids)
        trial = result.model_copy(update={"status": "OK", "items": (*result.items, item)})
        if defer_budget or estimator.estimate_text(trial.serialized()) <= query.token_budget:
            result = trial
        else:
            budget_excluded = True
        if len(result.items) == query.top_k:
            break
    if budget_excluded:
        result = result.model_copy(update={"truncated": True,
            "status": "OK" if result.items else "BUDGET_EXHAUSTED"})
    return result


def lexical_rank(entries: tuple[KnowledgeEntry, ...], query: KnowledgeQuery) -> tuple[KnowledgeEntry, ...]:
    scored = [(text_score(e, query.text), e) for e in entries]
    scored = [(score, e) for score, e in scored if score or query.knowledge_ids or not query.text.strip()]
    scored.sort(key=lambda pair: (-pair[0], pair[1].knowledge_id))
    return tuple(e for _, e in scored)
