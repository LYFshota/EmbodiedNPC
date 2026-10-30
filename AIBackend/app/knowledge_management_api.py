"""Local developer management only; NPC cognition cannot acquire this capability."""

from datetime import datetime, timezone
import json
import hashlib
import hmac
import secrets
import uuid
from typing import Literal

from fastapi import APIRouter, Depends, HTTPException, Request
from fastapi.responses import JSONResponse
from pydantic import Field, model_validator

from .model_api_config_store import _reject_duplicates
from .local_access import LocalBackendSession
from .world_knowledge import KnowledgeModel, KnowledgeEntry, KnowledgeAccess
from .world_knowledge_store import KnowledgeStoreError, KNOWLEDGE_MAX_BYTES, KnowledgeCatalog
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
    access_mode: str = Field(default="default", pattern="^(default|restricted|public)$")
    usage: Literal["constant", "retrieval"] | None = None
    keywords: tuple[str, ...] | None = Field(default=None, max_length=32)
    source: str | None = Field(default=None, min_length=1, max_length=240)
    preserve_access: bool = False
    character_ids: tuple[str, ...] = Field(default=(), max_length=32)
    scene_ids: tuple[str, ...] = Field(default=(), max_length=16)

    @model_validator(mode="after")
    def validate_edit(self):
        if not self.name.strip() or not self.content.strip() or not self.knowledge_type.strip():
            raise ValueError("KNOWLEDGE_REQUIRED_FIELDS")
        KnowledgeEntry.bounded_attributes(self.attributes)
        if self.keywords is not None:
            KnowledgeEntry.bounded_terms(self.keywords)
        if self.source is not None:
            KnowledgeEntry.nonblank(self.source)
        if len(set(self.related_ids)) != len(self.related_ids):
            raise ValueError("KNOWLEDGE_RELATED_DUPLICATE")
        return self


class KnowledgeImportCatalog(KnowledgeCatalog):
    # Transfers must explicitly declare the complete format, never adopt legacy overrides.
    schema_version: Literal[2]

    @model_validator(mode="after")
    def complete_references(self):
        ids = {e.knowledge_id for e in self.entries}
        if any(ref not in ids or ref == e.knowledge_id for e in self.entries for ref in e.related_ids):
            raise ValueError("KNOWLEDGE_RELATED_INVALID")
        return self


async def parse_input(request, domain):
    if request.headers.get("content-type", "").split(";")[0] != "application/json":
        raise HTTPException(415, detail="KNOWLEDGE_JSON_REQUIRED")
    raw = bytearray()
    async for chunk in request.stream():
        raw.extend(chunk)
        if len(raw) > KNOWLEDGE_MAX_BYTES:
            raise HTTPException(413, detail="KNOWLEDGE_INPUT_TOO_LARGE")
    try:
        document = domain.model_validate(json.loads(raw, object_pairs_hook=_reject_duplicates))
        document.model_dump_json()  # Reject escaped lone surrogates before preview or writes.
        return document
    except (ValueError, RecursionError):
        raise HTTPException(422, detail="KNOWLEDGE_EDIT_INVALID") from None


class KnowledgeManagement:
    def __init__(self, resolver, indexer=None):
        self.resolver, self.store, self.indexer = resolver, resolver.store, indexer
        self.auth = KnowledgeAdminSession(self.store)
        self._preview_key = secrets.token_bytes(32)

    def trusted_scopes(self):
        deployment = self.resolver.deployment
        if deployment is None or not deployment.bindings:
            raise HTTPException(503, detail="KNOWLEDGE_DEPLOYMENT_UNAVAILABLE")
        roles = sorted({b.character_id for b in deployment.bindings})
        return deployment, roles

    def index_status(self, bundle, entries):
        constant = sum(e.usage == "constant" for e in entries)
        entries = tuple(e for e in entries if e.usage == "retrieval")
        if self.indexer is None:
            return dict(mode="disabled", ready=0, total=len(entries), constant=constant, lexical_only=len(entries))
        provider = self.indexer.provider
        mode = getattr(provider, "mode", "running")
        try:
            valid = (self.indexer.index.load_valid(entries, bundle.bundle_version, knowledge_space(provider),
                     getattr(provider, "dimensions", None)) if self.indexer.index.path.exists() else {})
            return dict(mode=mode, ready=len(valid), total=len(entries), constant=constant, lexical_only=len(entries)-len(valid))
        except Exception:
            return dict(mode="unavailable", ready=0, total=len(entries), constant=constant, lexical_only=len(entries))

    def catalog(self):
        bundle, current, entries = self.store.snapshot()
        deployment, roles = self.trusted_scopes()
        template_ids = set(current.template_ids)
        referenced_by = {}
        for entry in entries:
            for key in entry.related_ids:
                if key != entry.knowledge_id:
                    referenced_by.setdefault(key, []).append(dict(knowledge_id=entry.knowledge_id, name=entry.name))
        items = [dict(entry=entry.model_dump(mode="json"), has_default=entry.knowledge_id in template_ids,
                      hidden=False, origin="current", referenced_by=referenced_by.get(entry.knowledge_id, []))
                 for entry in entries]
        return dict(schema_version=2, storage_mode="current", revision=current.revision,
            bundle_version=bundle.bundle_version, items=items,
            scopes=dict(deployment_id=deployment.deployment_id, character_ids=roles,
                        scene_ids=[deployment.scene_id] if deployment.scene_id else []),
            limits=dict(name=160, content=2000, knowledge_type=80, attribute_keys=32,
                        attribute_key_length=80, attributes_chars=2000, related_ids=16, keywords=32, keyword_length=120, source=240),
            index=self.index_status(bundle, entries))

    def save(self, edit, knowledge_id=None):
        bundle, current, entries = self.store.snapshot()
        if current.revision != edit.expected_revision:
            raise HTTPException(409, detail="KNOWLEDGE_REVISION_CONFLICT")
        by_id = {e.knowledge_id: e for e in entries}
        if knowledge_id is not None and knowledge_id not in by_id:
            raise HTTPException(404, detail="KNOWLEDGE_ID_UNKNOWN")
        deployment, roles = self.trusted_scopes()
        old = by_id.get(knowledge_id)
        if edit.preserve_access:
            if old is None:
                raise HTTPException(422, detail="KNOWLEDGE_SCOPE_INVALID")
            selected, scenes = old.access.character_ids, old.access.scene_ids
        elif edit.access_mode == "default":
            selected, scenes = roles, ()
        else:
            selected, scenes = edit.character_ids, edit.scene_ids
            if ((edit.access_mode == "restricted" and not selected)
                    or (edit.access_mode == "public" and selected) or not set(selected).issubset(roles)
                    or len(selected) != len(set(selected)) or len(scenes) != len(set(scenes))
                    or any(s != deployment.scene_id or deployment.scene_id is None for s in scenes)):
                raise HTTPException(422, detail="KNOWLEDGE_SCOPE_INVALID")
        if any(ref not in by_id or ref == knowledge_id for ref in edit.related_ids):
            raise HTTPException(422, detail="KNOWLEDGE_RELATED_INVALID")
        access = (old.access if edit.preserve_access else KnowledgeAccess(
            mode="public" if edit.access_mode == "public" else "restricted",
            character_ids=selected, deployment_ids=(deployment.deployment_id,), scene_ids=scenes))
        # Legacy clients cannot express public. An ordinary edit must preserve it.
        if old and old.access.mode == "public" and edit.access_mode == "default":
            access = old.access
        if knowledge_id is None:
            occupied = set(by_id) | set(current.template_ids)
            knowledge_id = "custom." + uuid.uuid4().hex
            while knowledge_id in occupied:
                knowledge_id = "custom." + uuid.uuid4().hex
        entry = KnowledgeEntry(knowledge_id=knowledge_id, name=edit.name.strip(), content=edit.content.strip(),
            knowledge_type=edit.knowledge_type.strip(), attributes=edit.attributes, related_ids=edit.related_ids,
            keywords=edit.keywords if edit.keywords is not None else (old.keywords if old else ()),
            source=edit.source if edit.source is not None else (old.source if old else "custom"),
            version=old.version if old else 1,
            updated_at=datetime.now(timezone.utc), access=access,
            usage=edit.usage or (old.usage if old else "retrieval"))
        self.store.manage("upsert", entry=entry, expected_revision=edit.expected_revision)
        return knowledge_id

    def _preview_token(self, package, revision):
        deployment, _ = self.trusted_scopes()
        document = dict(package=package.model_dump(mode="json"), revision=revision,
                        deployment=deployment.model_dump(mode="json"))
        raw = json.dumps(document, ensure_ascii=False, sort_keys=True, separators=(",", ":"), allow_nan=False)
        return hmac.new(self._preview_key, raw.encode("utf-8"), hashlib.sha256).hexdigest()

    def _replacement(self, package, current):
        # The installed template manifest and local revision belong to this Store.
        # A foreign package cannot claim ownership of local default entries.
        replacement = KnowledgeCatalog.model_validate(package.model_dump(mode="python")).model_copy(update={
            "revision": current.revision + 1, "updated_at": datetime.now(timezone.utc),
            "template_ids": current.template_ids})
        if len(replacement.model_dump_json(indent=2).encode("utf-8")) + 1 > KNOWLEDGE_MAX_BYTES:
            raise HTTPException(413, detail="KNOWLEDGE_INPUT_TOO_LARGE")
        return replacement

    def preview(self, package):
        _, current, entries = self.store.snapshot()
        self._replacement(package, current)  # Check the exact persisted representation too.
        before, after = ({e.knowledge_id: e for e in values} for values in (entries, package.entries))
        added, deleted = sorted(after.keys()-before.keys()), sorted(before.keys()-after.keys())
        modified, unchanged = [], []
        for key in sorted(before.keys() & after.keys()):
            old, new = before[key].model_dump(mode="json"), after[key].model_dump(mode="json")
            if old == new:
                unchanged.append(key)
            else:
                modified.append(dict(knowledge_id=key, fields=[field for field in old if old[field] != new[field]]))
        deployment, roles = self.trusted_scopes()
        warnings = []
        for entry in package.entries:
            access = entry.access
            for code, values in (
                ("UNKNOWN_CHARACTER_IDS", sorted(set(access.character_ids)-set(roles))),
                ("OTHER_DEPLOYMENT_IDS", sorted(set(access.deployment_ids)-{deployment.deployment_id})),
                ("OTHER_SCENE_IDS", sorted(set(access.scene_ids)-{deployment.scene_id})),
            ):
                if values:
                    warnings.append(dict(code=code, knowledge_id=entry.knowledge_id, values=values))
            if deployment.deployment_id not in access.deployment_ids or (access.scene_ids and deployment.scene_id not in access.scene_ids):
                warnings.append(dict(code="NOT_APPLICABLE_HERE", knowledge_id=entry.knowledge_id, values=[]))
        return dict(revision=current.revision, preview_token=self._preview_token(package, current.revision),
            added=added, modified=modified, unchanged=unchanged, deleted=deleted, warnings=warnings,
            replacement_count=len(package.entries), scope_notice="KNOWLEDGE_ONLY_NO_PERSONA_SCENE_MEMORY_CHANGES")

    def import_catalog(self, package, revision, token):
        _, current, _ = self.store.snapshot()
        if revision != current.revision:
            raise KnowledgeStoreError("KNOWLEDGE_REVISION_CONFLICT")
        if not hmac.compare_digest(token, self._preview_token(package, revision)):
            raise KnowledgeStoreError("KNOWLEDGE_PREVIEW_INVALID")
        replacement = self._replacement(package, current)
        self.store.replace(replacement, expected_revision=revision)

    def change(self, operation, knowledge_id, revision):
        bundle, current, entries = self.store.snapshot()
        if current.revision != revision:
            raise HTTPException(409, detail="KNOWLEDGE_REVISION_CONFLICT")
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
            raise HTTPException(409 if str(error) in {"KNOWLEDGE_REVISION_CONFLICT", "KNOWLEDGE_STORE_BUSY", "KNOWLEDGE_PREVIEW_INVALID"} else 422, detail=str(error)) from None
        except OSError:
            raise HTTPException(503, detail="KNOWLEDGE_STORE_UNAVAILABLE") from None
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
            bundle, current, entries = service.store.snapshot()
            return dict(revision=current.revision, index=service.index_status(bundle, entries))
        except (KnowledgeStoreError, OSError):
            raise HTTPException(503, detail="KNOWLEDGE_STORE_UNAVAILABLE") from None

    @router.get("/export")
    async def export():
        try:
            current = service.store.snapshot()[1]
            return JSONResponse(current.model_dump(mode="json"), headers={"Cache-Control": "no-store",
                "Content-Disposition": 'attachment; filename="world_knowledge.json"'})
        except (KnowledgeStoreError, OSError):
            raise HTTPException(503, detail="KNOWLEDGE_STORE_UNAVAILABLE") from None

    @router.post("/import/preview")
    async def preview(request: Request):
        package = await parse_input(request, KnowledgeImportCatalog)
        try:
            return JSONResponse(service.preview(package), headers={"Cache-Control": "no-store"})
        except (KnowledgeStoreError, OSError):
            raise HTTPException(503, detail="KNOWLEDGE_STORE_UNAVAILABLE") from None

    @router.post("/import")
    async def import_catalog(request: Request):
        package = await parse_input(request, KnowledgeImportCatalog)
        raw_revision = request.headers.get("X-Knowledge-Revision", "")
        token = request.headers.get("X-Knowledge-Preview", "")
        if not raw_revision.isascii() or not raw_revision.isdigit() or len(raw_revision) > 20 or len(token) != 64 or any(c not in "0123456789abcdef" for c in token):
            raise HTTPException(422, detail="KNOWLEDGE_PREVIEW_REQUIRED")
        return mutate(lambda: service.import_catalog(package, int(raw_revision), token))

    @router.post("/restore-all")
    async def restore_all(request: Request):
        edit = await parse_input(request, RevisionInput)
        def action():
            service.store.manage("restore-all", expected_revision=edit.expected_revision)
        return mutate(action)

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
