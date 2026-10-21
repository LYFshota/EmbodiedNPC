"""Deployment-only builtin resource binding; saved documents remain untouched."""

import os
from pathlib import Path


def resolve_embedding_resources(config):
    # The regular development/Editor bootstrap has no Portable data-root contract.
    if config.provider_type != "builtin" or not os.environ.get("EMBODIEDNPC_DATA_ROOT"):
        return config
    root = Path(os.environ["EMBODIEDNPC_DATA_ROOT"])
    directory = os.environ.get("EMBODIEDNPC_BUILTIN_EMBEDDING_DIR")
    if not root.is_absolute() or not directory or not Path(directory).is_absolute():
        raise ValueError("PORTABLE_RESOURCE_DIRECTORY_INVALID")
    return config.model_copy(update={"resource_directory": str(Path(directory).resolve())})
