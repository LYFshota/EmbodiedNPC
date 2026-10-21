"""Explicit new private/Portable candidate assembly. No data/configuration migration or ZIP.

Run with development Python; all three input directories are read-only sources.
"""
import argparse
import json
from pathlib import Path
import shutil


def assemble(destination, python_runtime, models, player, *, without_builtin_embedding=False):
    destination = destination.absolute()
    if destination.exists(): raise ValueError("Destination must be a new directory; never overwrite a candidate")
    source = Path(__file__).resolve().parents[1]
    for path in (python_runtime / "python.exe", player / "EmbodiedNPC.exe"):
        if not path.is_file(): raise ValueError("Required input resource missing: " + path.name)
    spec = json.loads((source / "app/builtin_embedding_resources.json").read_text(encoding="utf-8"))
    if not without_builtin_embedding and (models is None or any(not (models / name).is_file() for name in spec["files"])): raise ValueError("Embedding resource input incomplete")
    if destination.is_relative_to(source.parent): raise ValueError("Build outside the source workspace")
    backend = destination / "Backend"
    backend.mkdir(parents=True)
    # Application source/resources only, never a recursively copied development data directory.
    for path in (source / "app").rglob("*"):
        if path.is_file() and path.suffix in (".py", ".json") and "__pycache__" not in path.parts:
            target = backend / "app" / path.relative_to(source / "app")
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(path, target)
    shutil.copytree(python_runtime, backend / "python", ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
    if without_builtin_embedding:
        (backend / "portable_resource_policy.json").write_text(
            json.dumps({"schema_version": 1, "builtin_embedding": "not_bundled"}), encoding="utf-8")
    else:
        for name in (*spec["files"], "LICENSE", "Notice", "Gemma-Terms.html", "Gemma-Prohibited-Use-Policy.html", "CONVERSION_AND_SOURCE.md"):
            path = models / name
            if not path.is_file(): continue
            target = backend / "models/embeddinggemma-300m-q4" / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(path, target)
    shutil.copytree(player, destination / "UnityPlayer", ignore=shutil.ignore_patterns(
        "*DoNotShip*", "UserData", "build-result.json", "*.log", "*.sqlite*", "knowledge_admin_session.json"))
    for name in ("launcher.py", "unified.py", "start.cmd", "stop.cmd", "restart.cmd"):
        shutil.copy2(source / "portable" / name, backend / name)
    for name in ("Start.cmd", "Stop.cmd"):
        shutil.copy2(source / "portable/root" / name, destination / name)
    shutil.copy2(source / "portable/README.md", destination / "PORTABLE-START.txt")
    return destination


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--destination", type=Path, required=True)
    parser.add_argument("--python-runtime", type=Path, required=True)
    parser.add_argument("--models", type=Path)
    parser.add_argument("--without-builtin-embedding", action="store_true")
    parser.add_argument("--player", type=Path, required=True)
    args = parser.parse_args()
    print(assemble(args.destination, args.python_runtime.resolve(), args.models.resolve() if args.models else None, args.player.resolve(),
                   without_builtin_embedding=args.without_builtin_embedding))
