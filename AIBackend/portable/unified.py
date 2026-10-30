"""Copy into Backend/. Root Start.cmd orchestrates the existing B1 owner and B2 Player.

No model configuration, migration, force-kill or HTTP/AI readiness here.
"""
import argparse
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import traceback
import builtins
from datetime import datetime, timezone

sys.dont_write_bytecode = True
BACKEND = Path(__file__).resolve().parent
sys.path.insert(0, str(BACKEND))
import launcher as owner

# Windows Player Application.Quit(int); reserved for explicit Title AI apply consent.
RESTART_TO_APPLY_AI_CONFIG = 73
MAX_AI_CONFIG_RESTARTS = 1
_diagnostic_log = None


def print(*args, **kwargs):
    """Timestamp existing supervisor messages, without logging environment or exception inputs."""
    line = datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z") + f" pid={os.getpid()} " + " ".join(map(str, args))
    builtins.print(line, **kwargs)
    if _diagnostic_log is not None:
        try:
            if not _diagnostic_log.exists() or _diagnostic_log.stat().st_size < 1024 * 1024:
                with _diagnostic_log.open("a", encoding="utf-8") as output:
                    output.write(line + "\n")
        except OSError:
            pass  # Existing ownership and shutdown must not depend on optional diagnostic I/O.


class LaunchError(RuntimeError):
    pass


def process_api():
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.GetProcessTimes.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4
    kernel.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
    kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    kernel.WaitForSingleObject.restype = wintypes.DWORD
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    return kernel


def handle_identity(kernel, handle, pid):
    """A Popen handle stays authoritative even while its PID becomes unqueryable."""
    if kernel.WaitForSingleObject(handle, 0) == 0: return None
    created, exited, system, user = (wintypes.FILETIME() for _ in range(4))
    size = wintypes.DWORD(32768)
    path = ctypes.create_unicode_buffer(size.value)
    for step, query in (
        ("GetProcessTimes", lambda: kernel.GetProcessTimes(handle, *[ctypes.byref(x) for x in (created, exited, system, user)])),
        ("QueryFullProcessImageNameW", lambda: kernel.QueryFullProcessImageNameW(handle, 0, path, ctypes.byref(size))),
    ):
        if not query():
            code = ctypes.get_last_error()
            if kernel.WaitForSingleObject(handle, 0) == 0: return None
            error = LaunchError("UNIFIED_PROCESS_IDENTITY_UNAVAILABLE")
            error.winerror, error.step = code, step
            raise error
    return dict(pid=int(pid), created=(created.dwHighDateTime << 32) | created.dwLowDateTime, image=str(Path(path.value).resolve()))


def identity(pid):
    """Read-only external identity proof; never authorizes a kill by PID."""
    kernel = process_api()
    handle = kernel.OpenProcess(0x100000 | 0x1000, False, int(pid))
    if not handle:
        if ctypes.get_last_error() == 87: return None  # PID is no longer present.
        error = LaunchError("UNIFIED_PROCESS_IDENTITY_UNAVAILABLE")
        error.winerror, error.step = ctypes.get_last_error(), "OpenProcess"
        raise error
    try:
        return handle_identity(kernel, handle, pid)
    finally:
        kernel.CloseHandle(handle)


def owned_identity(process):
    # Do not reopen a just-created/exiting child by PID. Popen owns and closes this handle.
    return handle_identity(process_api(), process._handle, process.pid)


def diagnostic(stage, error):
    """No exception messages/args, source lines, locals, config or environment values."""
    chain, seen = [], set()
    while error is not None and id(error) not in seen and len(chain) < 4:
        seen.add(id(error))
        chain.append(dict(type=type(error).__name__, winerror=getattr(error, "winerror", None),
            step=getattr(error, "step", None), frames=[dict(file=Path(f.filename).name,
            line=f.lineno, function=f.name) for f in traceback.extract_tb(error.__traceback__)[-5:]]))
        error = error.__cause__ or error.__context__
    print("UNIFIED_DIAGNOSTIC " + json.dumps(dict(stage=stage, chain=chain)), file=sys.stderr, flush=True)


def retry_metadata(stage, action, timeout=1.0):
    """Retry only diagnosed Windows sharing/access/lock and identity-query failures."""
    deadline, delay = time.monotonic() + timeout, .01
    reported = False
    while True:
        try: return action()
        except Exception as error:
            current, transient, seen = error, False, set()
            while current is not None and id(current) not in seen:
                seen.add(id(current))
                transient |= (os.name == "nt" and getattr(current, "winerror", None) in (5, 6, 32, 33, 87))
                current = current.__cause__ or current.__context__
            if not reported:
                diagnostic(stage, error)
                reported = True
            remaining = deadline - time.monotonic()
            if not transient or remaining <= 0: raise
            # Conditional, bounded backoff after a real error; no unconditional startup delay.
            time.sleep(min(delay, remaining))
            delay = min(delay * 2, .1)


def publish(path, state):
    return retry_metadata("publish_" + state["state"], lambda: owner.private_record(path, state))


def publish_optional(path, state):
    try:
        publish(path, state)
        return True
    except Exception:
        print("UNIFIED_RECORD_DEFERRED: 实例记录暂不可用；入口仍持有子进程句柄和锁，关闭游戏后将按原所有权请求停止本次 Backend。", flush=True)
        return False


def wait_player(process, game):
    failed = False
    while True:
        try:
            if game.poll() is not None: return failed
            if process.poll() is not None and not failed:
                print("UNIFIED_BACKEND_EXITED: Backend 已退出；游戏仍受本入口管理，请正常关闭游戏。没有自动重启。", flush=True)
                failed = True
            game.wait(timeout=.2)
        except subprocess.TimeoutExpired: pass
        except KeyboardInterrupt:
            print("UNIFIED_PLAYER_STILL_MANAGED: 游戏仍在运行；入口保留句柄和锁，请正常关闭游戏。", flush=True)


def remove_owned_record(path, state):
    if not path.exists(): return
    try:
        current = retry_metadata("read_cleanup", lambda: owner.read_record(path))
        same = (state.get("backend_instance") is not None and current.get("backend_instance") == state["backend_instance"])
        same |= (state.get("backend") is not None and current.get("backend") == state["backend"])
        if same and current.get("data_root") == state["data_root"] and current.get("backend_directory") == state["backend_directory"]:
            retry_metadata("remove_record", path.unlink)
    except Exception as error:
        diagnostic("cleanup_record", error)
        print("UNIFIED_STALE_RECORD: 子进程已处理；实例记录未清理，请保留日志。", flush=True)


def alive(value):
    return bool(value) and identity(value["pid"]) == value


def layout(backend):
    game = backend.parent
    player = game / "UnityPlayer/EmbodiedNPC.exe"
    root = game / "UserData"
    if root.resolve() != root:
        raise LaunchError("UNIFIED_DATA_ROOT_REDIRECTED")
    required = [backend / "python/python.exe", backend / "launcher.py", backend / "app/main.py", player,
                game / "UnityPlayer/EmbodiedNPC_Data/globalgamemanagers", backend / "app/builtin_embedding_resources.json"]
    if any(not path.is_file() for path in required):
        raise LaunchError("UNIFIED_RESOURCE_MISSING")
    spec = json.loads((backend / "app/builtin_embedding_resources.json").read_text(encoding="utf-8"))
    if any(not (backend / "models/embeddinggemma-300m-q4" / name).is_file() for name in spec["files"]):
        raise LaunchError("UNIFIED_MODEL_RESOURCES_MISSING")
    return game, root, player


def environment(root, port):
    values = os.environ.copy()
    for key in list(values):
        if key.startswith(("EMBODIEDNPC_", "CANONICAL_EMBEDDING_", "OLLAMA_", "DEEPSEEK_", "OPENAI_",
                           "ANTHROPIC_", "GEMINI_", "GOOGLE_", "HF_", "HUGGINGFACE_", "TRANSFORMERS_", "PYTHON")):
            values.pop(key, None)
    values.update(EMBODIEDNPC_DATA_ROOT=str(root),
                  EMBODIEDNPC_KNOWLEDGE_ADMIN_FILE=str(root / "runtime/knowledge_admin_session.json"),
                  EMBODIEDNPC_KNOWLEDGE_ADMIN_URL=f"http://127.0.0.1:{port}", PYTHONDONTWRITEBYTECODE="1")
    return values


def record_path(root):
    return owner.contained(root, root / "runtime/unified-instance.json")


def backend_record(root, backend, process=None):
    value = owner.read_record(owner.runtime_paths(root)[1])
    if (value.get("schema_version") != 1 or value.get("data_root") != str(root) or
        value.get("backend_directory") != str(backend) or not isinstance(value.get("instance_id"), str) or len(value["instance_id"]) != 64):
        raise LaunchError("UNIFIED_BACKEND_OWNERSHIP_MISMATCH")
    if process is not None and (process.poll() is not None or value.get("pid") != process.pid):
        raise LaunchError("UNIFIED_BACKEND_OWNERSHIP_MISMATCH")
    return value


def controlled_stop(root, backend, state, timeout):
    if state.get("schema_version") != 1 or state.get("data_root") != str(root) or state.get("backend_directory") != str(backend):
        raise LaunchError("UNIFIED_OWNERSHIP_MISMATCH")
    if state.get("state") in ("launching_player", "player_identity_pending") and not state.get("player") and not state.get("player_pending_pid"):
        raise LaunchError("UNIFIED_PLAYER_IDENTITY_UNCONFIRMED")
    # A still-running Player must retain its Backend; Stop.cmd never closes the game or its queues.
    if alive(state.get("player")):
        raise LaunchError("UNIFIED_PLAYER_STILL_RUNNING")
    if state.get("player_pending_pid") and identity(state["player_pending_pid"]) is not None:
        raise LaunchError("UNIFIED_PLAYER_STILL_RUNNING")
    lock_path = owner.runtime_paths(root)[0]
    if not lock_path.exists(): return
    with owner.DataLock(lock_path, create=False) as lock:
        if lock.acquire(): return
    try:
        current = backend_record(root, backend)
    except FileNotFoundError:
        raise owner.PortableError("PORTABLE_INSTANCE_NOT_READY") from None
    expected = state.get("backend_instance")
    if expected is None:
        # A startup timeout may precede B1's instance publication. Prove the exact original spawn.
        if not alive(state.get("backend")) or current.get("pid") != state["backend"]["pid"]:
            raise LaunchError("UNIFIED_BACKEND_OWNERSHIP_MISMATCH")
        expected = current["instance_id"]
    owner.stop(root, backend, timeout, expected_instance=expected)


def stop(root, backend, timeout):
    path = record_path(root)
    if not path.exists():
        print("UNIFIED_NOT_OWNED: 没有本入口的实例记录；未停止其他 Backend。", flush=True)
        return
    # A live supervisor retains authoritative Popen handles even if its JSON is stale.
    guard_path = root / "runtime/unified.lock"
    if guard_path.exists():
        with owner.DataLock(guard_path, create=False) as guard:
            if not guard.acquire(): raise LaunchError("UNIFIED_STILL_SUPERVISING")
    state = retry_metadata("read_stop_record", lambda: owner.read_record(path))
    controlled_stop(root, backend, state, timeout)
    # Do not delete a newer launch's record if it starts just after B1 released its lock.
    remove_owned_record(path, state)


def start(root, backend, player, port, startup_timeout, stop_timeout):
    try:
        owner.writable(root)
        owner.writable(owner.contained(root, root / "runtime"))
        owner.writable(owner.contained(root, root / "logs"))
        global _diagnostic_log
        _diagnostic_log = owner.contained(root, root / "logs/portable.log")
    except OSError:
        raise LaunchError("UNIFIED_DATA_UNWRITABLE") from None
    guard_path = owner.contained(root, root / "runtime/unified.lock")
    with owner.DataLock(guard_path, create=True) as guard:
        if not guard.acquire(): raise LaunchError("UNIFIED_ALREADY_RUNNING")
        path = record_path(root)
        if path.exists():
            previous = retry_metadata("read_previous_record", lambda: owner.read_record(path))
            if alive(previous.get("player")) or (previous.get("player_pending_pid") and identity(previous["player_pending_pid"]) is not None):
                raise LaunchError("UNIFIED_PLAYER_STILL_RUNNING")
            if previous.get("state") in ("launching_player", "player_identity_pending") and not previous.get("player") and not previous.get("player_pending_pid"):
                raise LaunchError("UNIFIED_PLAYER_IDENTITY_UNCONFIRMED")
        # B1's data lock remains the final writer exclusion; never attach to an existing Backend.
        with owner.DataLock(owner.runtime_paths(root)[0], create=True) as data_lock:
            if not data_lock.acquire(): raise LaunchError("PORTABLE_ALREADY_RUNNING")
        restarts = 0
        while True:
            values = environment(root, port)
            state = dict(schema_version=1, data_root=str(root), backend_directory=str(backend),
                         state="starting", backend=None, backend_instance=None, player=None, restart_count=restarts)
            log = owner.contained(root, root / "logs" / f"backend-start_{time.time_ns()}.log")
            began = time.monotonic()
            with log.open("xb") as output:
                print("正在启动随包 Backend；不会更改模型配置…", flush=True)
                try:
                    process = subprocess.Popen([str(backend / "python/python.exe"), "-X", "utf8", "-B", str(backend / "launcher.py"), "start", "--port", str(port)],
                        cwd=backend, env=values, stdin=subprocess.DEVNULL, stdout=output, stderr=subprocess.STDOUT,
                        creationflags=subprocess.CREATE_NO_WINDOW)
                except OSError:
                    raise LaunchError("UNIFIED_BACKEND_START_FAILED") from None
            game = None
            try:
                try:
                    state["backend"] = retry_metadata("backend_identity", lambda: owned_identity(process))
                except Exception as error:
                    # B1's fresh capability will be bound to this exact live Popen below.
                    diagnostic("backend_identity_deferred", error)
                publish(path, state)
                next_message = began + 5
                while True:
                    if process.poll() is not None:
                        text = log.read_text(encoding="utf-8", errors="replace")
                        if "PORTABLE_PORT_UNAVAILABLE" in text: raise LaunchError("PORTABLE_PORT_UNAVAILABLE")
                        if "PORTABLE_ALREADY_RUNNING" in text: raise LaunchError("PORTABLE_ALREADY_RUNNING")
                        raise LaunchError("UNIFIED_BACKEND_START_FAILED")
                    try:
                        current = backend_record(root, backend, process)
                        state["backend_instance"] = current["instance_id"]
                        if current.get("state") == "listening": break
                    except FileNotFoundError: pass
                    except OSError as error:
                        if os.name != "nt" or getattr(error, "winerror", None) not in (5, 32, 33): raise
                        diagnostic("read_backend_record", error)
                    except LaunchError as error:
                        # A crash can leave the previous owner's file while our child is still importing.
                        # Wait for this exact live spawn to publish; never attach to that stale owner.
                        if str(error) != "UNIFIED_BACKEND_OWNERSHIP_MISMATCH": raise
                    if time.monotonic() - began > startup_timeout: raise LaunchError("UNIFIED_BACKEND_START_TIMEOUT")
                    if time.monotonic() >= next_message:
                        print(f"Backend 仍在准备（已等待 {int(time.monotonic()-began)} 秒）；尚未启动游戏。", flush=True)
                        next_message += 5
                    time.sleep(.1)
                state["state"] = "launching_player"
                publish(path, state)
                print(f"Backend 已监听 127.0.0.1:{port}（{time.monotonic()-began:.1f}s 采样）；正在打开主菜单，AI 就绪由游戏检查。", flush=True)
                player_values = values.copy()
                supervisor = identity(os.getpid())
                player_values.update(EMBODIEDNPC_UNIFIED_RESTART="1" if restarts < MAX_AI_CONFIG_RESTARTS else "0",
                    EMBODIEDNPC_UNIFIED_PID=str(supervisor["pid"]),
                    EMBODIEDNPC_UNIFIED_CREATED=str(supervisor["created"]))
                try:
                    game = subprocess.Popen([str(player), "--backend-url", f"ws://127.0.0.1:{port}/ws", "-logFile", str(root / ("logs/Player.log" if restarts == 0 else "logs/Player-restarted.log"))],
                        cwd=player.parent, env=player_values, stdin=subprocess.DEVNULL)
                except OSError:
                    raise LaunchError("UNIFIED_PLAYER_START_FAILED") from None
                state["player_pending_pid"] = game.pid
                # Publication is diagnostic/recovery metadata, not ownership of the live child.
                # Keep both Popen handles and the OS guard until normal Player exit.
                publish_optional(path, state)
                try:
                    state["player"] = retry_metadata("player_identity", lambda: owned_identity(game))
                    if state["player"] is not None:
                        state.update(state="running", player_pending_pid=None)
                    else:
                        state["state"] = "player_identity_pending"
                except Exception as error:
                    diagnostic("player_identity_deferred", error)
                    state["state"] = "player_identity_pending"
                published = publish_optional(path, state)
                failed = wait_player(process, game)
                print(f"Player 已退出（code={game.returncode}）；正在正常停止本次 Backend。", flush=True)
                state["player"] = None
                state["player_pending_pid"] = None
                state["state"] = "stopping"
                publish_optional(path, state)
                controlled_stop(root, backend, state, stop_timeout)
                remove_owned_record(path, state)
                print("本次 Backend 已停止；数据保留。", flush=True)
                state["state"] = "stopped"
                if game.returncode == RESTART_TO_APPLY_AI_CONFIG:
                    if failed or not published:
                        raise LaunchError("UNIFIED_RESTART_UNCONFIRMED")
                    if restarts >= MAX_AI_CONFIG_RESTARTS:
                        raise LaunchError("UNIFIED_RESTART_LIMIT")
                    # controlled_stop proved B1 lock release. Retain unified.lock across both cycles.
                    try:
                        process.wait(timeout=stop_timeout)
                    except subprocess.TimeoutExpired:
                        raise LaunchError("UNIFIED_RESTART_STOP_FAILED") from None
                    if process.returncode:
                        raise LaunchError("UNIFIED_RESTART_STOP_FAILED")
                    restarts += 1
                    print("正在重新启动本包 Backend 和游戏以应用全部已保存 AI 配置…", flush=True)
                    continue
                return 1 if failed or game.returncode or not published else 0
            except (Exception, KeyboardInterrupt) as error:
                diagnostic(state["state"], error)
                if state["state"] == "stopped":
                    print("本次子进程已处理，未继续重启；请检查错误后使用 Start.cmd。", flush=True)
                elif state["state"] == "stopping":
                    # The bounded normal stop already failed. Retain recovery state; do not wait twice.
                    print("UNIFIED_STOP_INCOMPLETE: 服务可能仍在运行。请稍后使用根目录 Stop.cmd；不会强杀。", flush=True)
                else:
                    # Recover in this same supervisor, not by adopting a PID from a file.
                    # KeyboardInterrupt or any later exception must not abandon a live Player.
                    if game is not None:
                        print("UNIFIED_RECOVERING: 游戏仍由本入口管理，请正常关闭；随后停止本次 Backend。", flush=True)
                        wait_player(process, game)
                    state.update(player=None, player_pending_pid=None, state="stopping")
                    if state.get("backend_instance") is None and process.poll() is None:
                        try: state["backend_instance"] = backend_record(root, backend, process)["instance_id"]
                        except (OSError, ValueError, LaunchError): pass
                    publish_optional(path, state)
                    try:
                        controlled_stop(root, backend, state, stop_timeout)
                        remove_owned_record(path, state)
                    except (Exception, KeyboardInterrupt) as stop_error:
                        diagnostic("recovery_stop", stop_error)
                        print("UNIFIED_STOP_INCOMPLETE: 服务可能仍在运行。关闭游戏后使用根目录 Stop.cmd；不会强杀。", flush=True)
                raise


def main():
    parser = argparse.ArgumentParser(description="Portable Backend/Player orchestration only")
    parser.add_argument("action", choices=("start", "stop"))
    parser.add_argument("--port", type=int, default=8000)
    parser.add_argument("--startup-timeout", type=float, default=60)
    parser.add_argument("--stop-timeout", type=float, default=20)
    args = parser.parse_args()
    messages = {
        "UNIFIED_RESOURCE_MISSING": "游戏资源不完整，请检查 UnityPlayer、Backend 和包内 Python。",
        "UNIFIED_DATA_UNWRITABLE": "UserData 无法创建或写入；请选择有写入权限的游戏目录，无需管理员权限。",
        "UNIFIED_MODEL_RESOURCES_MISSING": "随包内置 Embedding 资源缺失。",
        "UNIFIED_ALREADY_RUNNING": "本包正在启动、运行或停止；请勿重复打开。",
        "UNIFIED_PLAYER_STILL_RUNNING": "本份 UserData 的游戏仍在运行。请先正常关闭游戏，不要重复启动或停止服务。",
        "UNIFIED_STILL_SUPERVISING": "本入口仍持有进程句柄和锁；请正常关闭游戏并等待入口停止 Backend。",
        "UNIFIED_PLAYER_IDENTITY_UNCONFIRMED": "旧启动记录不足以证明 Player 已退出；未启动新游戏或停止 Backend，请保留现场检查。",
        "PORTABLE_ALREADY_RUNNING": "此 UserData 已有 Backend；不会接管或再打开游戏。",
        "PORTABLE_PORT_UNAVAILABLE": "端口已被占用；没有连接或停止其他服务。",
        "UNIFIED_BACKEND_START_FAILED": "Backend 启动失败，请查看 UserData/logs。",
        "UNIFIED_BACKEND_START_TIMEOUT": "Backend 启动超时，游戏尚未打开；请检查日志与受控 Stop.cmd 提示。",
        "UNIFIED_PLAYER_START_FAILED": "游戏启动失败；将尝试正常停止本次 Backend。",
        "PORTABLE_STOP_TIMEOUT": "Backend 正常停止超时，可能仍在运行；请稍后使用 Stop.cmd，不要强制结束。",
        "UNIFIED_RESTART_LIMIT": "本次入口已完成一次配置重启；为防止循环不再自动重开，请检查配置后使用 Start.cmd。",
        "UNIFIED_RESTART_UNCONFIRMED": "运行或所有权记录不完整，未继续重启；数据保留，请检查日志后使用 Start.cmd。",
        "UNIFIED_RESTART_STOP_FAILED": "旧 Backend 未正常结束，未继续重启；请检查日志，不要强制结束。",
        "PORTABLE_INSTANCE_NOT_READY": "本次 Backend 仍在初始化，暂不能确认停止会话；请稍后使用 Stop.cmd。",
    }
    try:
        if not 1 <= args.port <= 65535 or not 0 < args.startup_timeout <= 300 or not 0 < args.stop_timeout <= 120:
            raise LaunchError("UNIFIED_ARGUMENT_INVALID")
        # Stop does not require Player/model resources still to be installed.
        if args.action == "stop":
            root = BACKEND.parent / "UserData"
            if root.resolve() != root: raise LaunchError("UNIFIED_DATA_ROOT_REDIRECTED")
            stop(root, BACKEND, args.stop_timeout)
            return 0
        _, root, player = layout(BACKEND)
        return start(root, BACKEND, player, args.port, args.startup_timeout, args.stop_timeout)
    except (LaunchError, owner.PortableError) as error:
        diagnostic("command", error)
        print(f"{error}: {messages.get(str(error), '未完成操作；请检查本包路径与 UserData/runtime 实例状态，数据保留。')}", file=sys.stderr, flush=True)
    except (OSError, ValueError, KeyError, TypeError) as error:
        diagnostic("command", error)
        print("UNIFIED_DATA_OR_RUNTIME_UNAVAILABLE: 目录不可写、实例记录无效或运行环境不可用；没有回退目录或强杀进程。", file=sys.stderr, flush=True)
    except KeyboardInterrupt:
        print("UNIFIED_INTERRUPTED: 启动入口被中断；请正常退出游戏后使用 Stop.cmd。", file=sys.stderr, flush=True)
    except Exception as error:
        diagnostic("command", error)
        print("UNIFIED_START_FAILED: 请检查本包资源及 UserData/logs；没有强制结束其他进程。", file=sys.stderr, flush=True)
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
