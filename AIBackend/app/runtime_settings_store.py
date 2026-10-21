"""Small, independent versioned JSON store for saved Runtime Settings."""

from __future__ import annotations

import json
import os
import tempfile
import threading
from datetime import datetime, timezone
from pathlib import Path
from typing import Literal

from pydantic import ValidationError

from .runtime_settings import RuntimeSettings, RuntimeSettingsDocument, default_document


_PROCESS_LOCK = threading.RLock()
_MAX_DOCUMENT_BYTES = 256 * 1024


class RuntimeSettingsError(RuntimeError):
    def __init__(self, code: str) -> None:
        self.code = code
        super().__init__(code)


class SettingsInvalidDocument(RuntimeSettingsError):
    pass


class SettingsRevisionConflict(RuntimeSettingsError):
    pass


class SettingsStorageError(RuntimeSettingsError):
    pass


def default_settings_path() -> Path:
    """Machine-local bootstrap path, independent of EMBODIEDNPC_MEMORY_DB."""
    override = os.getenv("EMBODIEDNPC_SETTINGS_FILE")
    return Path(override) if override else Path(__file__).resolve().parents[1] / "data" / "runtime_settings.json"


def _reject_duplicate_keys(pairs: list[tuple[str, object]]) -> dict[str, object]:
    result: dict[str, object] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("DUPLICATE_JSON_KEY")
        result[key] = value
    return result


class RuntimeSettingsStore:
    def __init__(self, path: Path | None = None) -> None:
        self.path = Path(path) if path is not None else default_settings_path()

    def load(self) -> RuntimeSettingsDocument:
        with _PROCESS_LOCK:
            return self._load_unlocked()

    def load_with_source(self) -> tuple[RuntimeSettingsDocument, Literal["defaults", "persisted"]]:
        """Read the current document and its source together for the public API."""
        with _PROCESS_LOCK:
            return self._load_with_source_unlocked()

    def _load_unlocked(self) -> RuntimeSettingsDocument:
        return self._load_with_source_unlocked()[0]

    def _load_with_source_unlocked(self) -> tuple[RuntimeSettingsDocument, Literal["defaults", "persisted"]]:
        try:
            raw = self.path.read_bytes()
        except FileNotFoundError:
            return default_document(), "defaults"
        except OSError as exc:
            raise SettingsStorageError("SETTINGS_READ_FAILED") from exc
        if len(raw) > _MAX_DOCUMENT_BYTES:
            raise SettingsInvalidDocument("SETTINGS_DOCUMENT_TOO_LARGE")
        try:
            json.loads(raw, object_pairs_hook=_reject_duplicate_keys)
            return RuntimeSettingsDocument.model_validate_json(raw), "persisted"
        except (ValueError, ValidationError, UnicodeError):
            # Pydantic's detailed input errors may contain user text. Keep public errors code-only.
            raise SettingsInvalidDocument("SETTINGS_DOCUMENT_INVALID") from None

    def save(self, settings: RuntimeSettings, *, expected_revision: int) -> RuntimeSettingsDocument:
        if not isinstance(settings, RuntimeSettings):
            raise SettingsInvalidDocument("SETTINGS_VALUE_INVALID")
        try:
            # model_copy/model_construct can bypass Pydantic validators; recheck at the trust boundary.
            settings = RuntimeSettings.model_validate_json(settings.model_dump_json())
        except ValidationError:
            raise SettingsInvalidDocument("SETTINGS_VALUE_INVALID") from None
        if isinstance(expected_revision, bool) or not isinstance(expected_revision, int) or expected_revision < 0:
            raise SettingsInvalidDocument("SETTINGS_REVISION_INVALID")
        with _PROCESS_LOCK:
            current = self._load_unlocked()
            if current.revision != expected_revision:
                raise SettingsRevisionConflict("SETTINGS_REVISION_CONFLICT")
            document = RuntimeSettingsDocument(
                revision=current.revision + 1,
                updated_at=datetime.now(timezone.utc),
                settings=settings,
            )
            payload = document.model_dump_json(indent=2).encode("utf-8") + b"\n"
            if len(payload) > _MAX_DOCUMENT_BYTES:
                raise SettingsInvalidDocument("SETTINGS_DOCUMENT_TOO_LARGE")
            temporary: Path | None = None
            try:
                self.path.parent.mkdir(parents=True, exist_ok=True)
                with tempfile.NamedTemporaryFile(
                    mode="wb", dir=self.path.parent, prefix=".runtime_settings-",
                    suffix=".tmp", delete=False,
                ) as stream:
                    temporary = Path(stream.name)
                    stream.write(payload)
                    stream.flush()
                    os.fsync(stream.fileno())
                os.replace(temporary, self.path)
            except OSError as exc:
                raise SettingsStorageError("SETTINGS_WRITE_FAILED") from exc
            finally:
                if temporary is not None:
                    try:
                        temporary.unlink(missing_ok=True)
                    except OSError:
                        pass
            return document
