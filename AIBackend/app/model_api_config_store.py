"""Independent atomic JSON store for the Demo's two model connection slots."""

from __future__ import annotations

import json
import os
import tempfile
import threading
from datetime import datetime, timezone
from pathlib import Path

from pydantic import ValidationError

from .model_api_config import ModelApiConfig, ModelApiConfigDocument
from .model_profile_config import ModelConfigV2Document, ModelDocument, as_v2
from .private_config_permissions import restrict_config_file


_LOCK = threading.RLock()
_MAX_BYTES = 256 * 1024


class ModelConfigStoreError(RuntimeError):
    def __init__(self, code: str) -> None:
        self.code = code
        super().__init__(code)


def default_model_config_path() -> Path:
    override = os.getenv("EMBODIEDNPC_MODEL_CONFIG_FILE")
    return Path(override) if override else Path(__file__).resolve().parents[1] / "data" / "model_api_configs.json"


def _reject_duplicates(pairs: list[tuple[str, object]]) -> dict[str, object]:
    result: dict[str, object] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("MODEL_CONFIG_DUPLICATE_JSON_KEY")
        result[key] = value
    return result


class ModelApiConfigStore:
    """Persist revisions atomically; do not activate a live Provider.

    load() preserves v1/v2 documents. Explicit profile writes migrate only after
    revision/DTO validation. The process lock serializes this process only;
    deployment must separately prevent another process from writing this store.
    """

    def __init__(self, path: Path | None = None) -> None:
        self.path = Path(path) if path is not None else default_model_config_path()

    def load(self) -> ModelDocument | None:
        with _LOCK:
            return self._load_unlocked()

    def _load_unlocked(self) -> ModelDocument | None:
        try:
            raw = self.path.read_bytes()
        except FileNotFoundError:
            return None
        except OSError:
            raise ModelConfigStoreError("MODEL_CONFIG_READ_FAILED") from None
        if len(raw) > _MAX_BYTES:
            raise ModelConfigStoreError("MODEL_CONFIG_DOCUMENT_TOO_LARGE")
        try:
            value = json.loads(raw, object_pairs_hook=_reject_duplicates)
            domain = ModelConfigV2Document if isinstance(value, dict) and value.get("schema_version") == 2 else ModelApiConfigDocument
            return domain.model_validate_json(raw)
        except (ValueError, ValidationError, UnicodeError):
            raise ModelConfigStoreError("MODEL_CONFIG_DOCUMENT_INVALID") from None

    def save(self, config: ModelApiConfig, *, expected_revision: int) -> ModelApiConfigDocument:
        if not isinstance(config, ModelApiConfig):
            raise ModelConfigStoreError("MODEL_CONFIG_VALUE_INVALID")
        try:
            config = ModelApiConfig.model_validate_json(config.model_dump_json())
        except ValidationError:
            raise ModelConfigStoreError("MODEL_CONFIG_VALUE_INVALID") from None
        if isinstance(expected_revision, bool) or not isinstance(expected_revision, int) or expected_revision < 0:
            raise ModelConfigStoreError("MODEL_CONFIG_REVISION_INVALID")
        with _LOCK:
            previous = self._load_unlocked()
            if isinstance(previous, ModelConfigV2Document):
                raise ModelConfigStoreError("MODEL_CONFIG_V2_REQUIRED")
            revision = previous.revision if previous is not None else 0
            if revision != expected_revision:
                raise ModelConfigStoreError("MODEL_CONFIG_REVISION_CONFLICT")
            document = ModelApiConfigDocument(
                revision=revision + 1, updated_at=datetime.now(timezone.utc),
                main_model=config.main_model, embedding=config.embedding,
            )
            self._write_document(document)
            return document

    def update_profiles(self, *, expected_revision: int, transform) -> ModelConfigV2Document:
        """Migrate on a successful explicit write, never on load or failed validation."""
        if isinstance(expected_revision, bool) or not isinstance(expected_revision, int) or expected_revision < 0:
            raise ModelConfigStoreError("MODEL_CONFIG_REVISION_INVALID")
        with _LOCK:
            previous = self._load_unlocked()
            revision = previous.revision if previous else 0
            if revision != expected_revision:
                raise ModelConfigStoreError("MODEL_CONFIG_REVISION_CONFLICT")
            try:
                changed = transform(as_v2(previous))
                document = ModelConfigV2Document.model_validate_json(changed.model_copy(update={
                    "revision": revision + 1, "updated_at": datetime.now(timezone.utc),
                }).model_dump_json())
            except (ValueError, ValidationError, TypeError):
                raise ModelConfigStoreError("MODEL_CONFIG_VALUE_INVALID") from None
            self._write_document(document)
            return document

    def _write_document(self, document: ModelDocument) -> None:
        atomic_json_write(self.path, document.model_dump_json(indent=2).encode("utf-8") + b"\n")


def atomic_json_write(path: Path, payload: bytes) -> None:
    if len(payload) > _MAX_BYTES:
        raise ModelConfigStoreError("MODEL_CONFIG_DOCUMENT_TOO_LARGE")
    temporary: Path | None = None
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        with tempfile.NamedTemporaryFile(mode="wb", dir=path.parent, prefix=".model_api_configs-",
                                         suffix=".tmp", delete=False) as stream:
            temporary = Path(stream.name)
            restrict_config_file(temporary)
            stream.write(payload)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    except OSError:
        raise ModelConfigStoreError("MODEL_CONFIG_WRITE_FAILED") from None
    finally:
        if temporary is not None:
            try:
                temporary.unlink(missing_ok=True)
            except OSError:
                pass
