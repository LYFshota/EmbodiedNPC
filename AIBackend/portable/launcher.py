"""Copy beside Backend/app. Own only this Portable Backend, its lock and stop mailbox.

No model configuration initialization, migration or Unity process management lives here.
The Backend runs in this process, so the OS releases its data lock even after a crash.
"""

import argparse
import asyncio
import contextlib
import json
import os
from pathlib import Path
import secrets
import socket
import sys
import tempfile
import time
import builtins
from datetime import datetime, timezone


def print(*args, **kwargs):
    stamp = datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")
    builtins.print(f"{stamp} pid={os.getpid()}", *args, **kwargs)


class PortableError(RuntimeError):
    pass


FILES = {
    "EMBODIEDNPC_MEMORY_DB": "memory.sqlite3",
    "EMBODIEDNPC_MODEL_CONFIG_FILE": "model_api_configs.json",
    "EMBODIEDNPC_SETTINGS_FILE": "runtime_settings.json",
    "EMBODIEDNPC_CONTEXT_POLICY_FILE": "context_policy.json",
    "EMBODIEDNPC_KNOWLEDGE_FILE": "world_knowledge.json",
    "EMBODIEDNPC_KNOWLEDGE_INDEX_DB": "world_knowledge_index.sqlite3",
}


def contained(root, path):
    result = path.resolve()
    if not result.is_relative_to(root):
        raise PortableError("PORTABLE_PATH_ESCAPES_DATA_ROOT")
    return result


def data_root():
    value = os.environ.get("EMBODIEDNPC_DATA_ROOT", "")
    if not value or not Path(value).is_absolute():
        raise PortableError("PORTABLE_DATA_ROOT_REQUIRED")
    root = Path(value).resolve()
    if root == root.parent:
        raise PortableError("PORTABLE_DATA_ROOT_INVALID")
    return root


def writable(directory):
    directory.mkdir(parents=True, exist_ok=True)
    # Test actual create/write/delete rights, rather than trusting os.access on Windows.
    # tempfile's Windows EACCES/name retry can take minutes on a denied directory.
    probe = directory / (".portable-write-probe-" + secrets.token_hex(16))
    descriptor = os.open(probe, os.O_CREAT | os.O_EXCL | os.O_RDWR, 0o600)
    try:
        os.write(descriptor, b"portable-write-probe")
        os.fsync(descriptor)
    finally:
        os.close(descriptor)
        probe.unlink()


class DataLock:
    def __init__(self, path, *, create):
        self.stream = path.open("a+b" if create else "r+b")
        self.held = False
        if create and os.fstat(self.stream.fileno()).st_size == 0:
            self.stream.write(b"\0")
            self.stream.flush()

    def acquire(self):
        self.stream.seek(0)
        try:
            if os.name == "nt":
                import msvcrt
                msvcrt.locking(self.stream.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(self.stream.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError:
            return False
        self.held = True
        return True

    def release(self):
        if self.held:
            self.stream.seek(0)
            if os.name == "nt":
                import msvcrt
                msvcrt.locking(self.stream.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                import fcntl
                fcntl.flock(self.stream.fileno(), fcntl.LOCK_UN)
            self.held = False

    def close(self):
        try:
            self.release()
        finally:
            self.stream.close()

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()


def read_record(path):
    with path.open("rb") as stream:
        raw = stream.read(16385)
    if len(raw) > 16384:
        raise PortableError("PORTABLE_INSTANCE_RECORD_INVALID")
    value = json.loads(raw)
    if not isinstance(value, dict):
        raise PortableError("PORTABLE_INSTANCE_RECORD_INVALID")
    return value


def private_record(path, value):
    # This module has no application composition or DB initialization side effects.
    from app.model_api_config_store import atomic_json_write
    atomic_json_write(path, json.dumps(value).encode("utf-8"))


def prepare_environment(root, backend, port):
    for prefix in ("Backend", "runtime", "logs", "runtime/tmp", "runtime/hf-cache"):
        writable(contained(root, root / prefix))
    mapping = {key: str(contained(root, root / "Backend" / name)) for key, name in FILES.items()}
    for name in ("embedding_index_control.json",):
        contained(root, root / "Backend" / name)
    session = contained(root, root / "runtime/knowledge_admin_session.json")
    # Ignore inherited development data paths, credentials and provider bootstrap settings.
    for key in list(os.environ):
        if key.startswith(("EMBODIEDNPC_", "CANONICAL_EMBEDDING_", "OLLAMA_", "DEEPSEEK_",
                           "OPENAI_", "ANTHROPIC_", "GEMINI_", "GOOGLE_", "HF_", "HUGGINGFACE_",
                           "TRANSFORMERS_", "PYTHON")):
            os.environ.pop(key, None)
    os.environ.update(mapping)
    os.environ.update(
        EMBODIEDNPC_DATA_ROOT=str(root),
        EMBODIEDNPC_KNOWLEDGE_ADMIN_FILE=str(session),
        EMBODIEDNPC_KNOWLEDGE_ADMIN_URL=f"http://127.0.0.1:{port}",
        EMBODIEDNPC_BUILTIN_EMBEDDING_DIR=str(backend / "models/embeddinggemma-300m-q4"),
        HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1", PYTHONDONTWRITEBYTECODE="1",
        HF_HOME=str(root / "runtime/hf-cache"),
        TEMP=str(root / "runtime/tmp"), TMP=str(root / "runtime/tmp"), TMPDIR=str(root / "runtime/tmp"),
    )
    tempfile.tempdir = None
    sys.dont_write_bytecode = True
    # The current backend's unconfigured bootstrap remains unconfigured; no Mock JSON is written.
    sys.path.insert(0, str(backend))


def runtime_paths(root):
    return tuple(contained(root, root / "runtime" / name) for name in
                 ("backend.lock", "backend-instance.json", "backend-stop.json"))


async def serve(root, backend, port, listener, owner_path, request_path):
    import uvicorn
    from app.main import app  # Only after environment, exclusive data lock and socket reservation.

    instance = dict(schema_version=1, instance_id=secrets.token_hex(32), pid=os.getpid(),
                    data_root=str(root), backend_directory=str(backend), port=port, state="starting")
    private_record(owner_path, instance)
    server = uvicorn.Server(uvicorn.Config(app, host="127.0.0.1", port=port,
        loop="asyncio", ws="websockets", log_level="info", access_log=False, log_config=None))

    async def watch():
        ready = False
        while not server.should_exit:
            if server.started and not ready:
                try:
                    private_record(owner_path, {**instance, "state": "listening"})
                except Exception:
                    server.should_exit = True
                    raise PortableError("PORTABLE_INSTANCE_RECORD_UNAVAILABLE") from None
                ready = True  # This describes HTTP listening, never model/StartupReady.
            try:
                request = read_record(request_path)
                if secrets.compare_digest(str(request.get("instance_id", "")).encode(), instance["instance_id"].encode()):
                    server.should_exit = True
                    return
            except (OSError, ValueError, PortableError):
                pass
            await asyncio.sleep(.1)

    task = asyncio.create_task(watch())
    try:
        await server.serve(sockets=[listener])
        if not server.started:
            raise PortableError("PORTABLE_BACKEND_START_FAILED")
    finally:
        task.cancel()
        with contextlib.suppress(asyncio.CancelledError):
            await task
        for path in (owner_path, request_path):
            try:
                if read_record(path).get("instance_id") == instance["instance_id"]:
                    path.unlink()
            except (OSError, ValueError, PortableError):
                pass


def start(root, backend, port):
    if not (backend / "app/main.py").is_file():
        raise PortableError("PORTABLE_BACKEND_LAYOUT_INVALID")
    if root.is_relative_to(backend):
        raise PortableError("PORTABLE_DATA_ROOT_INSIDE_PROGRAM")
    writable(root)
    writable(contained(root, root / "runtime"))
    lock_path, owner_path, request_path = runtime_paths(root)
    with DataLock(lock_path, create=True) as lock:
        if not lock.acquire():
            raise PortableError("PORTABLE_ALREADY_RUNNING")
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
            if os.name == "nt":
                listener.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
            try:
                listener.bind(("127.0.0.1", port))
                listener.listen(128)
                listener.setblocking(False)
            except OSError:
                raise PortableError("PORTABLE_PORT_UNAVAILABLE") from None
            prepare_environment(root, backend, port)
            from app.portable_runtime import own_portable_data
            own_portable_data(root, lambda: lock.held)
            from app.private_config_permissions import restrict_config_file
            log = contained(root, root / "logs" / f"backend_{time.time_ns()}_{os.getpid()}.log")
            with log.open("x", encoding="utf-8", buffering=1) as output:
                restrict_config_file(log)
                print(f"Portable Backend: 127.0.0.1:{port}; logs: {log.name}", flush=True)
                with contextlib.redirect_stdout(output), contextlib.redirect_stderr(output):
                    asyncio.run(serve(root, backend, port, listener, owner_path, request_path))


def stop(root, backend, timeout, *, expected_instance=None):
    lock_path, owner_path, request_path = runtime_paths(root)
    try:
        lock = DataLock(lock_path, create=False)
    except FileNotFoundError:
        print("PORTABLE_NOT_RUNNING")
        return
    with lock:
        if lock.acquire():
            print("PORTABLE_NOT_RUNNING")  # Stale files/PIDs cannot authorize a stop.
            return
        try:
            owner = read_record(owner_path)
        except (OSError, ValueError):
            raise PortableError("PORTABLE_INSTANCE_NOT_READY") from None
        if (owner.get("schema_version") != 1 or owner.get("data_root") != str(root) or
                owner.get("backend_directory") != str(backend) or
                not isinstance(owner.get("instance_id"), str) or len(owner["instance_id"]) != 64):
            raise PortableError("PORTABLE_INSTANCE_OWNERSHIP_MISMATCH")
        if expected_instance is not None and owner["instance_id"] != expected_instance:
            raise PortableError("PORTABLE_INSTANCE_CHANGED")
        # The private mailbox names one random live instance; never signal/kill a PID or port.
        sys.path.insert(0, str(backend))
        private_record(request_path, {"instance_id": owner["instance_id"]})
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if lock.acquire():
                print("PORTABLE_STOPPED")
                return
            try:
                if read_record(owner_path).get("instance_id") != owner["instance_id"]:
                    raise PortableError("PORTABLE_INSTANCE_CHANGED")
            except FileNotFoundError:
                pass
            time.sleep(.1)
        raise PortableError("PORTABLE_STOP_TIMEOUT")


def main():
    parser = argparse.ArgumentParser(description="Portable Backend only; no Unity launcher or data migration.")
    parser.add_argument("action", choices=("start", "stop", "restart"))
    parser.add_argument("--port", type=int, default=8000)
    parser.add_argument("--timeout", type=float, default=20)
    args = parser.parse_args()
    sys.dont_write_bytecode = True
    try:
        if not 1 <= args.port <= 65535 or not 0 < args.timeout <= 120:
            raise PortableError("PORTABLE_ARGUMENT_INVALID")
        root, backend = data_root(), Path(__file__).resolve().parent
        if args.action in ("stop", "restart"):
            stop(root, backend, args.timeout)
        if args.action in ("start", "restart"):
            start(root, backend, args.port)
        return 0
    except PortableError as error:
        messages = {
            "PORTABLE_ALREADY_RUNNING": "同一 UserData 的 Backend 已在运行。",
            "PORTABLE_PORT_UNAVAILABLE": "端口不可用；没有停止或接管其他服务。",
            "PORTABLE_DATA_ROOT_REQUIRED": "需要绝对 EMBODIEDNPC_DATA_ROOT；请使用随包启动入口。",
        }
        print(f"{error}: {messages.get(str(error), 'Portable Backend 操作失败，原数据保留。')}", file=sys.stderr)
    except (OSError, ValueError):
        print("PORTABLE_DATA_OR_RUNTIME_UNAVAILABLE: 请检查路径、写入权限和日志；没有回退到其他目录。", file=sys.stderr)
    except Exception:
        # Never echo config, authorization documents, exception inputs or inherited secrets.
        print("PORTABLE_BACKEND_START_FAILED: 请检查私有日志和运行依赖。", file=sys.stderr)
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
