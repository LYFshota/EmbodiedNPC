# Windows Backend / unified portable launcher

Packaging copies `launcher.py`, `start.cmd`, `stop.cmd`, `restart.cmd` beside
`Backend/app`, `Backend/python` and `Backend/models`. Do not execute the source
templates as a development Backend; the existing `uvicorn app.main:app` path is unchanged.

`start.cmd` defaults the absolute data root to the package sibling `UserData`.
The unified root entry (implemented below) supplies `EMBODIEDNPC_DATA_ROOT` explicitly. The Python
entry requires this absolute variable; it never guesses from database presence.

```
Backend\start.cmd --port <chosen-loopback-port>
Backend\stop.cmd
Backend\restart.cmd --port <same-chosen-port>
```

Start runs the Backend in the foreground. Stop/restart must use the same package
entry and root. An exclusive OS byte lock lives at `UserData/runtime/backend.lock`;
private instance/stop records are only a mailbox for that live lock owner.
No PID/port is killed or adopted. A crash releases the OS lock; a leftover file
does not prevent starting again. Stop never prepares storage or rewrites config.

All six existing persistent-file variables map to `UserData/Backend`; index-control
keeps its model-config sibling path. Knowledge authorization maps to
`UserData/runtime/knowledge_admin_session.json`. Backend logs and dependency temp
files are under `UserData/logs` and `UserData/runtime`. Model weights stay in
`Backend/models/embeddinggemma-300m-q4`. Portable resource binding is in-memory;
no saved profile/revision/Key is rewritten. No Mock config is provisioned.

For a matching Backend/Player pair, pass the **same absolute** `EMBODIEDNPC_DATA_ROOT`,
`EMBODIEDNPC_KNOWLEDGE_ADMIN_FILE`, `EMBODIEDNPC_KNOWLEDGE_ADMIN_URL` to Player;
derive `--backend-url ws://127.0.0.1:<port>/ws` from the actual chosen port.
Do not infer AI readiness from `backend-instance.json`'s `listening` state.
Unity Portable preferences use this same root. The unified root entry below coordinates
startup and the bounded apply/restart flow documented at the end; it does not migrate saves
or provide a Launcher.exe.

## Unified package entry

Copy `root/Start.cmd` and `root/Stop.cmd` to the **game root**, and `unified.py` beside
`Backend/launcher.py`. Keep the root templates in their separate source subdirectory:
Windows filenames are case-insensitive, so root Start.cmd must not replace Backend/start.cmd.

Double-click root **Start.cmd**. It resolves its own package location and always uses
`<game>/UserData`, regardless of CWD or inherited DATA_ROOT. It checks required resources,
write permissions and ownership, starts B1, waits at most 60 seconds for B1's *listening*
record, then opens `UnityPlayer/EmbodiedNPC.exe`. During a slow startup the console reports
elapsed waiting every five seconds. This deliberately reuses the B2 Player without a build
or a second startup checker; it waits for HTTP listening, not AI/Provider readiness.
Title retains the existing configuration/Provider/WS checks and manual retry. If Backend
fails unexpectedly later, the entry reports it and does not automatically restart it or close
the Player. The explicit one-shot configuration apply/restart flow is described below.

Testing may explicitly use `Start.cmd --port <isolated-port>` (normal default: 127.0.0.1:8000).
Optional bounds are `--startup-timeout <seconds>` and `--stop-timeout <seconds>`.
Backend and Player receive the same root, authorization file and HTTP URL;
Player receives matching `--backend-url` and `-logFile UserData/logs/Player.log`.
No Mock, API key, profile selection or revision is initialized by this entry.

Leave the Start window open. After Player exits, B1's normal stop is invoked with the
captured instance capability. An additional small OS startup lock prevents two root entries;
B1's data lock remains the final writer exclusion. Player identity uses a live Windows
handle, creation time and image path, never a PID file alone. There is no kill-by-port/PID.

Root **Stop.cmd** is recovery for an entry-owned Backend after the Player has closed;
it refuses a live Player and will not adopt an independently started Backend or another root.
B1's Backend/stop.cmd remains its separate explicit maintenance entry. If stop times out,
the service may still be alive: inspect UserData/logs and retry controlled Stop later.
Forced console closure, crash and Windows logout are not guaranteed async cleanup paths.
Normal Backend stop does not prove Unity's unacknowledged in-memory Experiences were saved.

Runtime record publication and process identity queries have bounded, error-specific
Windows retries. A running supervisor retains its original Popen handles and OS lock
even when publication fails; leave it open and close the Player normally. Stop.cmd
refuses intervention while that supervisor is still active. An incomplete old launch
record without a trustworthy Player identity is refused for both Start and Stop;
it is not permission to adopt or terminate a process. UNIFIED_DIAGNOSTIC records
exception types, Win32 codes and stack locations without exception arguments or secrets.

`assemble.py --destination <new-folder> --python-runtime <bundled-python-dir>
--models <pinned-model-dir> --player <B2-build-dir>` assembles a new candidate without
overwriting an old one or copying UserData, developer data, keys or session tokens.
It is not a ZIP/release pipeline. Close both processes before copying an installed game.

## Early Prototype public preview

Use `assemble.py --without-builtin-embedding` when weights are not distributed.
It writes an explicit package resource policy so Start.cmd can reach first-time
configuration without weights. Select external Embedding or disable it before
saving the first Main configuration. The actual built-in provider still checks
its resources and fails explicitly if selected without the files; no session,
ownership, schema or runtime resource validation is bypassed.

This source release uses a newly built Windows Player from the cleaned public
candidate, not a historical Standalone. Validation scope is in
[release status](../../Docs/RELEASE_STATUS.md). Never distribute UserData.

## UserData backup and restore

Close the Player normally, confirm its Experiences have persistence acknowledgements,
and wait for the owned Backend to stop before copying the **entire UserData folder**.
Do not remove SQLite WAL files manually. Restore into a new, compatible installation
with no existing UserData; do not merge or overwrite a valuable target save.
Start the destination with its own Start.cmd so it obtains new process ownership and
a fresh local knowledge authorization session. Old session tokens are not backup credentials.

UserData may contain model API credentials. Its private file permissions are not
device-bound encryption: keep backups private and verify access permissions after copying.
Never include a player's UserData or runtime authorization files in a distributable ZIP.
Cross-user or clean-machine ACL restoration requires separate validation.

## Apply AI configuration from Title (Windows)

After the original v2 save-and-activate succeeds and runtime confirmation reports a
restart is required, Title offers **Restart and apply now / Later**. Consent exits
the owned Windows Player with reserved code **73**. The same unified supervisor
keeps its OS lock, stops only its captured B1 Backend instance, waits for lock
release and process exit, then starts the Backend and Player using the same
UserData and port. AI readiness is checked again by the original Title flow;
process start is not model or vector-index readiness.

Only one such restart is allowed per Start invocation. The restarted Player
explains that further changes apply on the next normal launch. Normal exit and
crash codes do not request restart. No restart intent file, recursive Start.cmd,
configuration rewriting, index rebuild or force-kill is used. Logs from the
restarted Player are in `UserData/logs/Player-restarted.log`.

The child-only `EMBODIEDNPC_UNIFIED_RESTART`, `EMBODIEDNPC_UNIFIED_PID` and
`EMBODIEDNPC_UNIFIED_CREATED` values advertise the live supervisor and one-shot
budget; they contain no credentials and do not authorize Backend stop. Unity
checks the supervisor creation time before offering exit. Direct Player launch,
old launchers and Unity Editor do not promise automatic reopening. Cafe saves
never use the restart exit request. A stop/start timeout ends the sequence and
keeps data; inspect the reported logs and use the existing owned Stop/Start
recovery flow, without killing or adopting another service.
On Windows, an ordinary copy can inherit the destination folder's broader permissions.
The current entry does not repair copied credential-file ACLs automatically; verify or
restore their private permissions before using real credentials in the restored installation.

## v0.1.0-preview distribution

The official Windows preview includes the eight pinned EmbeddingGemma-300M Q4 ONNX files and their Gemma distribution terms. Use `--models` with the prepared model directory. The optional no-weights assembly mode is not the default release. Main LLM configuration remains required.
