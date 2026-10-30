"""Local developer CLI. Run from AIBackend with python -m tools.manage_world_knowledge."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from app.world_knowledge import KnowledgeEntry
from app.world_knowledge_store import WorldKnowledgeStore, read_document


def main():
    parser = argparse.ArgumentParser(description="Manage the current knowledge catalog locally; initialize explicitly before first use")
    parser.add_argument("operation", choices=("initialize", "list", "upsert", "delete", "restore", "restore-all"))
    parser.add_argument("--file", type=Path, help="Override store path (use an isolated path during testing)")
    parser.add_argument("--entry", type=Path, help="Validated KnowledgeEntry JSON for upsert")
    parser.add_argument("--id", dest="knowledge_id")
    parser.add_argument("--expected-revision", type=int)
    args = parser.parse_args()
    store = WorldKnowledgeStore(args.file)
    try:
        if args.operation == "initialize":
            document = store.initialize()
            print(document.model_dump_json(indent=2))
        elif args.operation == "list":
            _, current, _ = store.snapshot()
            print(current.model_dump_json(indent=2))
        else:
            if args.expected_revision is None:
                parser.error("mutations require --expected-revision")
            entry = read_document(args.entry, KnowledgeEntry) if args.entry else None
            document = store.manage(args.operation, expected_revision=args.expected_revision,
                                    entry=entry, knowledge_id=args.knowledge_id)
            print(json.dumps({"revision": document.revision, "updated_at": document.updated_at.isoformat()}))
    except (OSError, ValueError) as error:
        parser.exit(1, f"Knowledge operation failed: {error}; use initialize explicitly if required.\n")


if __name__ == "__main__":
    main()
