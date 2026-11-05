"""Explicit new private/Portable candidate assembly. No data/configuration migration or ZIP.

Run with development Python; all three input directories are read-only sources.
"""
import argparse
import json
from pathlib import Path
import shutil
import zipfile


def assemble(destination, python_runtime, models, player):
    destination = destination.absolute()
    if destination.exists(): raise ValueError("Destination must be a new directory; never overwrite a candidate")
    source = Path(__file__).resolve().parents[1]
    for path in (python_runtime / "python.exe", player / "EmbodiedNPC.exe"):
        if not path.is_file(): raise ValueError("Required input resource missing: " + path.name)
    spec = json.loads((source / "app/builtin_embedding_resources.json").read_text(encoding="utf-8"))
    if any(not (models / name).is_file() for name in spec["files"]): raise ValueError("Embedding resource input incomplete")
    if destination.is_relative_to(source.parent): raise ValueError("Build outside the source workspace")
    backend = destination / "Backend"
    backend.mkdir(parents=True)
    # Application source/resources only, never a recursively copied development data directory.
    for path in (source / "app").rglob("*"):
        if path.is_file() and path.suffix in (".py", ".json") and "__pycache__" not in path.parts:
            target = backend / "app" / path.relative_to(source / "app")
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(path, target)
    # The Backend already supports this bundled resource location. Copy the one
    # authored menu used by Unity; do not introduce a second catalog.
    shutil.copy2(source.parent / "UnityClient/Assets/Resources/CafeSnackCatalog.json", backend / "app/knowledge/cafe_snacks.json")
    shutil.copytree(python_runtime, backend / "python", ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
    for name in (*spec["files"], "README.md", "LICENSE", "Gemma-Terms.html", "Gemma-Prohibited-Use-Policy.html", "Notice", "CONVERSION_AND_SOURCE.md"):
        path = models / name
        if not path.is_file(): continue
        target = backend / "models/embeddinggemma-300m-q4" / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, target)
    shutil.copytree(player, destination / "UnityPlayer", ignore=shutil.ignore_patterns(
        "*DoNotShip*", "UserData", "*.log", "*.sqlite*", "knowledge_admin_session.json"))
    for name in ("launcher.py", "unified.py", "start.cmd", "stop.cmd", "restart.cmd"):
        shutil.copy2(source / "portable" / name, backend / name)
    for name in ("Start.cmd", "Stop.cmd", "启动游戏.cmd"):
        shutil.copy2(source / "portable/root" / name, destination / name)
    shutil.copy2(source / "portable/README.md", backend / "PORTABLE-START.txt")
    shutil.copy2(source / "portable/root/README-试玩说明.txt", destination / "README-试玩说明.txt")
    return destination


def create_zip(stage, destination):
    """Archive the contents directly, so Extract-to-folder adds only one directory."""
    if (stage / "UserData").exists():
        raise ValueError("Never package player UserData")
    if destination.resolve().is_relative_to(stage.resolve()):
        raise ValueError("ZIP must be outside stage")
    with zipfile.ZipFile(destination, "x", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for path in sorted(stage.rglob("*")):
            if path.is_file():
                archive.write(path, path.relative_to(stage).as_posix())
    return destination


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--destination", type=Path, required=True)
    parser.add_argument("--python-runtime", type=Path, required=True)
    parser.add_argument("--models", type=Path, required=True)
    parser.add_argument("--player", type=Path, required=True)
    parser.add_argument("--zip", type=Path, help="Optional new ZIP, with package contents at archive root")
    args = parser.parse_args()
    stage = assemble(args.destination, args.python_runtime.resolve(), args.models.resolve(), args.player.resolve())
    print(create_zip(stage, args.zip) if args.zip else stage)
