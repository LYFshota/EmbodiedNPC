[简体中文](OPERATIONS.md) | [English](OPERATIONS.en.md)

# Local operations and data isolation

Run a single-worker Backend on your computer using the [Source Setup](SOURCE_SETUP.en.md) instructions. The default port is `127.0.0.1:8000`, and local runtime data lives under `AIBackend/data` in the active checkout.

## Model configuration and authorization

In TitleScene, choose Main and Embedding models under AI Configuration, save and activate, then restart Backend when prompted. The local `model_api_configs.json` can include API credentials, so keep that file private.

The Backend creates a local session file read automatically by the corresponding Unity Editor project. `GET /live` checks process liveness; protected health and WebSocket endpoints use session authorization.

## Isolated tests and multiple checkouts

Prepare isolated paths **before importing the Python application or starting the process**. The following example runs Mock in a temporary directory on port 8001:

```powershell
cd .\AIBackend
$testRunDir = Join-Path ([IO.Path]::GetTempPath()) ('EmbodiedNPC-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRunDir | Out-Null
$env:EMBODIEDNPC_MEMORY_DB = Join-Path $testRunDir 'memory.sqlite3'
$env:EMBODIEDNPC_SETTINGS_FILE = Join-Path $testRunDir 'runtime_settings.json'
$env:EMBODIEDNPC_MODEL_CONFIG_FILE = Join-Path $testRunDir 'model_api_configs.json'
$env:EMBODIEDNPC_KNOWLEDGE_FILE = Join-Path $testRunDir 'world_knowledge.json'
$env:EMBODIEDNPC_KNOWLEDGE_INDEX_DB = Join-Path $testRunDir 'world_knowledge_index.sqlite3'
$env:EMBODIEDNPC_KNOWLEDGE_ADMIN_FILE = Join-Path $testRunDir 'knowledge_admin_session.json'
$env:EMBODIEDNPC_CONTEXT_POLICY_FILE = Join-Path $testRunDir 'context_policy.json'
$env:EMBODIEDNPC_KNOWLEDGE_ADMIN_URL = 'http://127.0.0.1:8001'
$env:CANONICAL_EMBEDDING_ENABLED = 'false'
$env:EMBODIEDNPC_PROVIDER = 'mock'
.\.venv\Scripts\python.exe -m uvicorn app.main:app --host 127.0.0.1 --port 8001 --no-proxy-headers
```

When using a nondefault port with Unity, point its Backend URL and session lookup to the same instance. Run tests on disposable data, not player saves.

## Contributor verification

Run focused tests for the changed components, then distinguish static, Mock, real-model, Unity Editor and Windows Player results in your PR.

## Model concurrency

**Settings → Developer tools → Concurrency settings** configures total, foreground and background limits (1–32, each class at most the total). Defaults: cloud 6/6/2; Ollama 1/1/1. These limits share a single executor with foreground priority and background fairness. Save and Apply follows the existing configuration activation and restart flow.
