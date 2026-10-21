"""Opt-in real-process smoke. Only new external directories and owned loopback processes.

Run with the existing development Python; --python names the bundled interpreter.
Program code and pinned model files are staged; no development data is copied.
"""

import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request

from websockets.sync.client import connect

from app.builtin_embedding_provider import MODEL, REVISION, RESOURCE_SPEC
from app.model_api_config import EmbeddingConfig, MainModelConfig, ModelApiConfig
from app.model_profile_config import ModelConfigV2Document, MainProfile, EmbeddingProfile

SOURCE = Path(__file__).parents[1]
PROCESSES = []


def port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        value = s.getsockname()[1]
    assert value not in (8000, 18000)
    return value


def call(p, path, method="GET", body=None, root=None, token=None):
    assert p not in (8000, 18000)
    headers = {}
    if root is not None:
        if token is None:
            token = json.loads((root / "runtime/knowledge_admin_session.json").read_text())["token"]
        headers["Authorization"] = "Bearer " + token
    if body is not None:
        headers["Content-Type"] = "application/json"
    request = urllib.request.Request(f"http://127.0.0.1:{p}" + path, method=method, headers=headers,
        data=None if body is None else json.dumps(body).encode())
    try:
        with urllib.request.build_opener(urllib.request.ProxyHandler({})).open(request, timeout=30) as response:
            return response.status, json.load(response)
    except urllib.error.HTTPError as error:
        return error.code, json.loads(error.read())


def env(root):
    value = os.environ.copy()
    for name in list(value):
        if name.startswith(("EMBODIEDNPC_", "CANONICAL_EMBEDDING_", "OLLAMA_", "OPENAI_", "DEEPSEEK_",
                            "HF_", "PYTHON", "ANTHROPIC_", "GOOGLE_", "GEMINI_")):
            value.pop(name)
    value.update(EMBODIEDNPC_DATA_ROOT=str(root), PYTHONDONTWRITEBYTECODE="1", PYTHONUTF8="1")
    return value


def command(python, backend, root, action, p):
    return [str(python), "-B", str(backend / "launcher.py"), action, "--port", str(p), "--timeout", "15"]


def invoke(python, backend, root, action, p):
    return subprocess.run(command(python, backend, root, action, p), env=env(root), cwd=backend.parent,
        stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=25)


def start(python, backend, root, p, evidence):
    output = (evidence / f"process-{p}-{time.time_ns()}.log").open("wb")
    child = subprocess.Popen(command(python, backend, root, "start", p), env=env(root), cwd=backend.parent,
        stdout=output, stderr=subprocess.STDOUT, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    output.close()
    PROCESSES.append((child, backend, root, p))
    for _ in range(200):
        if child.poll() is not None:
            raise AssertionError(f"Owned startup failed, port={p}, exit={child.returncode}")
        try:
            owner = json.loads((root / "runtime/backend-instance.json").read_text())
            if owner["pid"] == child.pid and owner["state"] == "listening" and call(p, "/health")[0] == 200:
                return child
        except (OSError, ValueError):
            pass
        time.sleep(.1)
    raise AssertionError("Owned Backend startup timed out")


def stop(python, backend, root, p, child):
    result = invoke(python, backend, root, "stop", p)
    assert result.returncode == 0, result.stdout.decode("utf-8", errors="replace")
    child.wait(timeout=15)
    assert child.returncode == 0
    assert not (root / "runtime/knowledge_admin_session.json").exists()
    assert not (root / "runtime/backend-instance.json").exists()


def stage(package, model_source):
    backend = package / "Backend"
    shutil.copytree(SOURCE / "app", backend / "app", ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
    for name in ("launcher.py", "start.cmd", "stop.cmd", "restart.cmd"):
        shutil.copy2(SOURCE / "portable" / name, backend / name)
    for name in RESOURCE_SPEC["files"]:
        target = backend / "models" / MODEL / name
        target.parent.mkdir(parents=True, exist_ok=True)
        os.link(model_source / name, target)  # Actual package-relative files, not a path to old installation.
    return backend


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def copy_package(source, target):
    assert not target.exists()
    def copy_file(src, dst):
        # Only read-only model resources share disk blocks; UserData is a real copy.
        if Path(src).is_relative_to(source / "Backend/models"):
            return os.link(src, dst)
        return shutil.copy2(src, dst)
    shutil.copytree(source, target, copy_function=copy_file)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--scratch", type=Path, required=True)
    parser.add_argument("--python", type=Path, required=True)
    parser.add_argument("--models", type=Path, required=True)
    args = parser.parse_args()
    scratch = args.scratch.resolve()
    assert not scratch.exists() and not scratch.is_relative_to(SOURCE.parents[0])
    scratch.mkdir(parents=True)
    evidence = scratch / "evidence"
    evidence.mkdir()
    result = {}
    try:
        package = scratch / "原始 package"
        backend = stage(package, args.models)
        root = package / "UserData"
        p = port()
        bypass_root = scratch / "不可绕过锁 UserData"
        bypass = subprocess.run([str(args.python), "-B", "-c",
            "import sys;sys.path.insert(0,sys.argv[1]);import app.main", str(backend)],
            env=env(bypass_root), stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=15)
        assert bypass.returncode != 0 and b"PORTABLE_LAUNCH_REQUIRED" in bypass.stdout and not bypass_root.exists()
        first = start(args.python, backend, root, p, evidence)
        status, view = call(p, "/v2/model-config")
        assert status == 200 and not view["configured"] and view["revision"] == 0
        assert not (root / "Backend/model_api_configs.json").exists()
        assert not (backend / "data").exists()
        status, context = call(p, "/v1/context-policy")
        assert status == 200
        assert call(p, "/v1/context-policy", "PUT", dict(expected_revision=0,
            config=dict(mode="manual", target_messages=7)))[0] == 200
        status, catalog = call(p, "/v1/developer/knowledge", root=root)
        assert status == 200 and len(catalog["items"]) == 4
        assert call(p, "/v1/developer/knowledge", "POST", dict(expected_revision=0,
            name="Portable B1 synthetic", content="Private persistence fixture", knowledge_type="reference",
            access_mode="default", attributes={}, related_ids=[], character_ids=[], scene_ids=[]), root=root)[0] == 200
        payload = json.loads((SOURCE.parent / "Contracts/examples/valid/experience_append_request.json").read_text())
        with connect(f"ws://127.0.0.1:{p}/ws", proxy=None) as ws:
            ws.send(json.dumps(payload))
            ack = json.loads(ws.recv(timeout=10))
            assert ack["status"] == "inserted"
        result["T1"] = dict(result="PASS", port=p, configured=False, revision=0,
            no_mock_config=True, experience_ack=ack["status"], knowledge_created=True, context_policy_saved=True)
        stop(args.python, backend, root, p, first)

        # Explicit synthetic fixture ONLY after T1 confirmed an unconfigured fresh root.
        main_config = MainModelConfig(display_name="Explicit smoke Mock", provider_type="mock", model="fixed-greeting-v1")
        embedding = EmbeddingConfig(enabled=True, provider_type="builtin", model=MODEL, dimensions=768,
            model_version=REVISION, resource_directory=str(scratch / "不存在的旧开发安装" / MODEL))
        document = ModelConfigV2Document(revision=7, updated_at=datetime.now(timezone.utc),
            main_profiles=(MainProfile(profile_id="smoke-main", preset_id="custom", config=main_config),
                MainProfile(profile_id="inactive-cloud", preset_id="custom", config=MainModelConfig(
                    display_name="Synthetic inactive", provider_type="openai_compatible", model="synthetic",
                    base_url="https://synthetic.invalid/v1", api_key="synthetic-inactive-key-not-real"))),
            embedding_profiles=(EmbeddingProfile(profile_id="smoke-embedding", preset_id="builtin_gemma", config=embedding),),
            selected_main_profile_id="smoke-main", selected_embedding_profile_id="smoke-embedding",
            activation=ModelApiConfig(main_model=main_config, embedding=embedding))
        config = root / "Backend/model_api_configs.json"
        config.write_text(document.model_dump_json(indent=2), encoding="utf-8")
        original_config_hash = digest(config)
        second = start(args.python, backend, root, p, evidence)
        status, view = call(p, "/v2/model-config")
        assert status == 200 and view["revision"] == 7 and not view["restart_required"]
        assert view["selected_to_activate"]["embedding"]["resource_directory"] == str(backend / "models" / MODEL)
        assert call(p, "/v1/context-policy")[1]["applied"]["config"]["target_messages"] == 7
        assert len(call(p, "/v1/developer/knowledge", root=root)[1]["items"]) == 5
        with connect(f"ws://127.0.0.1:{p}/ws", proxy=None) as ws:
            ws.send(json.dumps(payload))
            assert json.loads(ws.recv(timeout=10))["status"] == "already_exists"
        stop(args.python, backend, root, p, second)
        assert digest(config) == original_config_hash
        result["T2"] = dict(result="PASS", revision=7, config_bytes_unchanged=True,
            context_policy_retained=True, knowledge_count=5, experience_replay="already_exists")

        relocated = scratch / "搬迁后的 copy" / "EmbodiedNPC"
        copy_package(package, relocated)  # Closed whole test package, including UserData; no reassembly.
        relocated_backend = relocated / "Backend"
        moved_root = relocated / "UserData"
        moved_port = port()
        moved = start(args.python, relocated_backend, moved_root, moved_port, evidence)
        status, view = call(moved_port, "/v2/model-config")
        assert status == 200 and not view["restart_required"] and view["revision"] == 7
        assert view["embedding_profiles"][0]["config"]["resource_directory"] == str(relocated_backend / "models" / MODEL)
        before_probe = digest(moved_root / "Backend/model_api_configs.json")
        status, probe = call(moved_port, "/v2/model-config/probe", "POST", dict(
            expected_revision=7, kind="embedding", profile_id="smoke-embedding", preset_id="builtin_gemma",
            key_action="retain", config=embedding.model_dump(exclude={"api_key"}),
            operation="connection", confirm_paid_requests=True))
        assert status == 200 and probe.get("success"), (status, probe)
        assert digest(moved_root / "Backend/model_api_configs.json") == before_probe == original_config_hash
        assert digest(config) == original_config_hash
        result["T3"] = dict(result="PASS", port=moved_port, real_builtin_probe=probe,
            effective_directory=str(relocated_backend / "models" / MODEL), config_bytes_unchanged=True,
            old_resource_path_missing=True, restart_required=False)

        # Same data root, different port; OS lock must refuse before importing app/main.
        refused = invoke(args.python, relocated_backend, moved_root, "start", port())
        assert refused.returncode != 0 and b"PORTABLE_ALREADY_RUNNING" in refused.stdout
        wrong_entry = invoke(args.python, backend, moved_root, "stop", moved_port)
        assert wrong_entry.returncode != 0 and b"OWNERSHIP_MISMATCH" in wrong_entry.stdout
        assert call(moved_port, "/health")[0] == 200
        other_root = scratch / "独立 UserData"
        other_port = port()
        other = start(args.python, backend, other_root, other_port, evidence)
        stop(args.python, relocated_backend, moved_root, moved_port, moved)
        assert call(other_port, "/health")[0] == 200
        # Simulate a crash of ONLY the Popen handle created here, retaining stale metadata/session.
        old_session = json.loads((other_root / "runtime/knowledge_admin_session.json").read_text())
        old_owner = json.loads((other_root / "runtime/backend-instance.json").read_text())
        other.kill()
        other.wait(timeout=10)
        recovered = start(args.python, backend, other_root, other_port, evidence)
        assert call(other_port, "/v1/developer/knowledge", root=other_root, token=old_session["token"])[0] == 403
        assert call(other_port, "/v1/developer/knowledge", root=other_root)[0] == 200
        (other_root / "runtime/backend-stop.json").write_text(json.dumps(dict(instance_id=old_owner["instance_id"])))
        time.sleep(.3)
        assert call(other_port, "/health")[0] == 200  # An old stop request cannot stop the new owner.
        (other_root / "runtime/backend-stop.json").write_text(json.dumps(dict(instance_id="无效旧请求")))
        time.sleep(.3)
        assert call(other_port, "/health")[0] == 200
        stop(args.python, backend, other_root, other_port, recovered)

        with socket.socket() as occupied:
            occupied.bind(("127.0.0.1", 0))
            occupied.listen()
            conflict_port = occupied.getsockname()[1]
            conflict_root = scratch / "端口冲突 UserData"
            conflict = invoke(args.python, backend, conflict_root, "start", conflict_port)
            assert conflict.returncode != 0 and b"PORTABLE_PORT_UNAVAILABLE" in conflict.stdout
            assert not (conflict_root / "Backend/memory.sqlite3").exists()
        readonly = scratch / "只读 UserData"
        readonly.mkdir()
        sid = subprocess.check_output(["whoami", "/user", "/fo", "csv", "/nh"]).decode().strip().split(",")[-1].strip('"')
        try:
            subprocess.run(["icacls", str(readonly), "/deny", f"*{sid}:(OI)(CI)(W)"], check=True, capture_output=True)
            denied = invoke(args.python, backend, readonly, "start", port())
            assert denied.returncode != 0 and b"PORTABLE_DATA_OR_RUNTIME_UNAVAILABLE" in denied.stdout
            assert not (readonly / "Backend").exists()
        finally:
            subprocess.run(["icacls", str(readonly), "/remove:d", "*" + sid], check=True, capture_output=True)
        result["T4"] = dict(result="PASS", same_root_rejected=True, independent_root=True,
            unowned_portable_composition_refused_before_store_init=True,
            wrong_entry_stop_rejected=True, other_process_unaffected=True, os_lock_recovers_after_kill=True,
            old_session_rejected=True, old_stop_request_ignored=True, port_conflict_refused=True,
            unwritable_root_refused=True)
    finally:
        for child, backend_dir, owned_root, owned_port in PROCESSES:
            if child.poll() is None:
                try:
                    stop(args.python, backend_dir, owned_root, owned_port, child)
                except Exception:
                    child.terminate()  # Only an exact owned Popen, never another process/PID from disk.
                    child.wait(timeout=10)
        (evidence / "smoke-results.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(result, ensure_ascii=True, indent=2))


if __name__ == "__main__":
    main()
