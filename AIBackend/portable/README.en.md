[简体中文](README.md) | [English](README.en.md)

# Windows portable launcher

This directory contains the Windows preview's Backend start, stop, configuration-restart and packaging scripts. Players normally double-click the package-root **启动游戏.cmd** (or `Start.cmd`) and follow `使用说明.html` to configure a model.

## Startup flow

The unified root launcher:

1. Finds the package and uses its `UserData` directory.
2. Checks required resources and write access, and obtains an available local port.
3. Starts the bundled Python Backend and waits for its listening record.
4. Opens `UnityPlayer/EmbodiedNPC.exe` with matching WebSocket and log settings.
5. Stops its owned Backend when the Player exits normally.

TitleScene performs Main/Embedding configuration, provider checks and game readiness. The launcher handles processes rather than model configuration.

## Runtime paths

| Path | Purpose |
| --- | --- |
| `Backend/app`, `Backend/python` | Backend application and bundled Python runtime |
| `Backend/models/embeddinggemma-300m-q4` | Local Embedding resources |
| `UserData/Backend` | Memory, knowledge and model configuration |
| `UserData/runtime` | Ownership locks, runtime records and private local sessions |
| `UserData/logs` | Backend and Unity Player logs |

The root entry defaults to an OS-assigned loopback port. Backend and Player receive the same URL and local session. Locks and live process handles tie stop operations to the process started by this package.

## Maintenance commands

Within the packaged Backend directory:

```text
Backend\start.cmd --port <local-port>
Backend\stop.cmd
Backend\restart.cmd --port <local-port>
```

The root `Stop.cmd` is available for controlled recovery after the Player closes. Normal gameplay exits through the launcher.

## Applying AI configuration

After changing and activating Main or Embedding settings, TitleScene may request one controlled exit, Backend restart and Player relaunch. The process retains the same UserData and resumes TitleScene's connection checks.

## Packaging and backups

`assemble.py` stages the bundled runtime, Embedding files and Unity Player:

```text
assemble.py --destination <folder> --python-runtime <python-dir> --models <model-dir> --player <build-dir>
```

A new ZIP can be built with `--zip <new-file.zip>`. Distributable packages include their required notices and exclude personal UserData and credentials.

For backup, exit the Player and stop Backend normally, then copy the whole `UserData` directory including SQLite companion files.

[Project README](../../README.en.md) · [Backend](../README.en.md) · [Source Setup](../../Docs/SOURCE_SETUP.en.md)
