"""Local developer management only; NPC cognition cannot acquire this capability."""

from datetime import datetime, timezone
import json
import uuid

from fastapi import APIRouter, Depends, HTTPException, Request
from fastapi.responses import JSONResponse
from pydantic import Field, model_validator

from .model_api_config_store import _reject_duplicates
from .local_access import LocalBackendSession
from .world_knowledge import KnowledgeModel, KnowledgeEntry, KnowledgeAccess
from .world_knowledge_store import KnowledgeStoreError
from .knowledge_embeddings import knowledge_space

# Retain the old import/constructor for Knowledge clients and focused tests.
KnowledgeAdminSession = LocalBackendSession

class RevisionInput(KnowledgeModel):
    expected_revision: int = Field(ge=0, strict=True)


class KnowledgeEdit(RevisionInput):
    name: str = Field(min_length=1, max_length=160)
    content: str = Field(min_length=1, max_length=2000)
    knowledge_type: str = Field(default="reference", min_length=1, max_length=80)
    attributes: dict = Field(default_factory=dict, max_length=32)
    related_ids: tuple[str, ...] = Field(default=(), max_length=16)
    access_mode: str = Field(default="default", pattern="^(default|restricted)$")
    character_ids: tuple[str, ...] = Field(default=(), max_length=32)
    scene_ids: tuple[str, ...] = Field(default=(), max_length=16)

    @model_validator(mode="after")
    def validate_edit(self):
        if not self.name.strip() or not self.content.strip() or not self.knowledge_type.strip():
            raise ValueError("KNOWLEDGE_REQUIRED_FIELDS")
        KnowledgeEntry.bounded_attributes(self.attributes)
        if len(set(self.related_ids)) != len(self.related_ids):
            raise ValueError("KNOWLEDGE_RELATED_DUPLICATE")
        return self


async def parse_input(request, domain):
    if request.headers.get("content-type", "").split(";")[0] != "application/json":
        raise HTTPException(415, detail="KNOWLEDGE_JSON_REQUIRED")
    raw = bytearray()
    async for chunk in request.stream():
        raw.extend(chunk)
        if len(raw) > 256 * 1024:
            raise HTTPException(413, detail="KNOWLEDGE_INPUT_TOO_LARGE")
    try:
        return domain.model_validate(json.loads(raw, object_pairs_hook=_reject_duplicates))
    except (ValueError, RecursionError):
        raise HTTPException(422, detail="KNOWLEDGE_EDIT_INVALID") from None


class KnowledgeManagement:
    def __init__(self, resolver, indexer=None):
        self.resolver, self.store, self.indexer = resolver, resolver.store, indexer
        self.auth = KnowledgeAdminSession(self.store)

    def trusted_scopes(self):
        deployment = self.resolver.deployment
        if deployment is None or not deployment.bindings:
            raise HTTPException(503, detail="KNOWLEDGE_DEPLOYMENT_UNAVAILABLE")
        roles = sorted({b.character_id for b in deployment.bindings})
        return deployment, roles

    def index_status(self, bundle, entries):
        if self.indexer is None:
            return dict(mode="disabled", ready=0, total=len(entries))
        provider = self.indexer.provider
        mode = getattr(provider, "mode", "running")
        try:
            valid = (self.indexer.index.load_valid(entries, bundle.bundle_version, knowledge_space(provider),
                     getattr(provider, "dimensions", None)) if self.indexer.index.path.exists() else {})
            return dict(mode=mode, ready=len(valid), total=len(entries))
        except Exception:
            return dict(mode="unavailable", ready=0, total=len(entries))

    def catalog(self):
        bundle, overrides, entries = self.store.snapshot()
        deployment, roles = self.trusted_scopes()
        defaults = {e.knowledge_id: e for e in bundle.entries}
        by_id = {e.knowledge_id: e for e in entries}
        for key in overrides.deleted_ids:
            if key in defaults:
                by_id[key] = defaults[key]  # Hidden builtin remains manageable/restorable.
        overridden = {e.knowledge_id for e in overrides.entries}
        items = []
        for key, entry in sorted(by_id.items()):
            items.append(dict(entry=entry.model_dump(mode="json"), has_default=key in defaults,
                hidden=key in overrides.deleted_ids, origin="override" if key in overridden else
                "builtin" if key in defaults else "custom",
                referenced_by=[dict(knowledge_id=e.knowledge_id, name=e.name) for e in entries
                               if key in e.related_ids and e.knowledge_id != key]))
        return dict(revision=overrides.revision, bundle_version=bundle.bundle_version, items=items,
            scopes=dict(deployment_id=deployment.deployment_id, character_ids=roles,
                        scene_ids=[deployment.scene_id] if deployment.scene_id else []),
            limits=dict(name=160, content=2000, knowledge_type=80, attribute_keys=32,
                        attribute_key_length=80, attributes_chars=2000, related_ids=16),
            index=self.index_status(bundle, entries))

    def save(self, edit, knowledge_id=None):
        bundle, overrides, entries = self.store.snapshot()
        if overrides.revision != edit.expected_revision:
            raise HTTPException(409, detail="KNOWLEDGE_REVISION_CONFLICT")
        by_id = {e.knowledge_id: e for e in entries}
        if knowledge_id is not None and knowledge_id not in by_id:
            raise HTTPException(404, detail="KNOWLEDGE_ID_UNKNOWN")
        deployment, roles = self.trusted_scopes()
        if edit.access_mode == "default":
            selected, scenes = roles, ()
        else:
            selected, scenes = edit.character_ids, edit.scene_ids
            if (not selected or not set(selected).issubset(roles)
                    or len(selected) != len(set(selected)) or len(scenes) != len(set(scenes))
                    or any(s != deployment.scene_id or deployment.scene_id is None for s in scenes)):
                raise HTTPException(422, detail="KNOWLEDGE_SCOPE_INVALID")
        if any(ref not in by_id or ref == knowledge_id for ref in edit.related_ids):
            raise HTTPException(422, detail="KNOWLEDGE_RELATED_INVALID")
        old = by_id.get(knowledge_id)
        if knowledge_id is None:
            occupied = set(by_id) | set(overrides.deleted_ids) | {e.knowledge_id for e in bundle.entries}
            knowledge_id = "custom." + uuid.uuid4().hex
            while knowledge_id in occupied:
                knowledge_id = "custom." + uuid.uuid4().hex
        entry = KnowledgeEntry(knowledge_id=knowledge_id, name=edit.name.strip(), content=edit.content.strip(),
            knowledge_type=edit.knowledge_type.strip(), attributes=edit.attributes, related_ids=edit.related_ids,
            keywords=old.keywords if old else (), source="Developer Panel", version=old.version if old else 1,
            updated_at=datetime.now(timezone.utc), access=KnowledgeAccess(character_ids=selected,
                deployment_ids=(deployment.deployment_id,), scene_ids=scenes))
        self.store.manage("upsert", entry=entry, expected_revision=edit.expected_revision)
        return knowledge_id

    def change(self, operation, knowledge_id, revision):
        bundle, overrides, entries = self.store.snapshot()
        if overrides.revision != revision:
            raise HTTPException(409, detail="KNOWLEDGE_REVISION_CONFLICT")
        if operation == "restore" and knowledge_id not in {e.knowledge_id for e in bundle.entries}:
            raise HTTPException(422, detail="KNOWLEDGE_NO_DEFAULT")
        if operation == "delete" and any(knowledge_id in e.related_ids for e in entries if e.knowledge_id != knowledge_id):
            raise HTTPException(409, detail="KNOWLEDGE_REFERENCED_UNLINK_FIRST")
        self.store.manage(operation, knowledge_id=knowledge_id, expected_revision=revision)


def create_knowledge_management_router(service):
    router = APIRouter(prefix="/v1/developer/knowledge", dependencies=[Depends(service.auth.require)])

    def response(selected=None):
        result = service.catalog()
        result["selected_id"] = selected
        return JSONResponse(result, headers={"Cache-Control": "no-store"})

    def mutate(action):
        try:
            selected = action()
        except KnowledgeStoreError as error:
            raise HTTPException(409 if str(error) == "KNOWLEDGE_REVISION_CONFLICT" else 422, detail=str(error)) from None
        if service.indexer is not None:
            service.indexer.notify()
        return response(selected)

    @router.get("")
    async def listing():
        try:
            return response()
        except (KnowledgeStoreError, OSError):
            raise HTTPException(503, detail="KNOWLEDGE_STORE_UNAVAILABLE") from None

    @router.get("/status")
    async def status():
        try:
            bundle, overrides, entries = service.store.snapshot()
            return dict(revision=overrides.revision, index=service.index_status(bundle, entries))
        except (KnowledgeStoreError, OSError):
            raise HTTPException(503, detail="KNOWLEDGE_STORE_UNAVAILABLE") from None

    @router.get("/{knowledge_id}")
    async def detail(knowledge_id: str):
        result = service.catalog()
        item = next((i for i in result["items"] if i["entry"]["knowledge_id"] == knowledge_id), None)
        if item is None:
            raise HTTPException(404, detail="KNOWLEDGE_ID_UNKNOWN")
        return dict(revision=result["revision"], item=item)

    @router.post("")
    async def create(request: Request):
        edit = await parse_input(request, KnowledgeEdit)
        return mutate(lambda: service.save(edit))

    @router.put("/{knowledge_id}")
    async def update(knowledge_id: str, request: Request):
        edit = await parse_input(request, KnowledgeEdit)
        def action():
            service.save(edit, knowledge_id)
            return knowledge_id
        return mutate(action)

    @router.delete("/{knowledge_id}")
    async def delete(knowledge_id: str, request: Request):
        edit = await parse_input(request, RevisionInput)
        return mutate(lambda: service.change("delete", knowledge_id, edit.expected_revision))

    @router.post("/{knowledge_id}/restore")
    async def restore(knowledge_id: str, request: Request):
        edit = await parse_input(request, RevisionInput)
        def action():
            service.change("restore", knowledge_id, edit.expected_revision)
            return knowledge_id
        return mutate(action)

    return router
