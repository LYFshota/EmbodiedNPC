"""In-process witness for the launcher's live OS lock, not a data/config manager."""

import os
from pathlib import Path

_owner = None


def own_portable_data(root, lock_is_held):
    global _owner
    _owner = (os.getpid(), Path(root).resolve(), lock_is_held)


def require_portable_owner():
    value = os.environ.get("EMBODIEDNPC_DATA_ROOT")
    if not value:
        return  # Unchanged development bootstrap.
    if (_owner is None or _owner[0] != os.getpid() or
            _owner[1] != Path(value).resolve() or not _owner[2]()):
        raise RuntimeError("PORTABLE_LAUNCH_REQUIRED")
