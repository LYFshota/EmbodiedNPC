[简体中文](SOURCE_SETUP.md) | [English](SOURCE_SETUP.en.md)

# Run from source (Windows / Unity Editor)

This guide starts EmbodiedNPC from its Unity and Python sources. For gameplay only, download the Windows preview from the [project README](../README.en.md) and follow its bundled illustrated guide `使用说明.html`.

## Requirements

- Unity **6000.5.10f1**, activated through Unity Hub
- Git for Windows, used by Unity package dependencies
- Python **3.12 x64**
- Network access for Unity packages, Python packages and the chosen model service

## Start the Backend

From the repository root:

```powershell
cd .\AIBackend
py -3.12 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
.\.venv\Scripts\python.exe -m pip check
.\.venv\Scripts\python.exe -m uvicorn app.main:app --host 127.0.0.1 --port 8000 --no-proxy-headers
```

Backend listens locally on `127.0.0.1:8000` by default. For testing in several checkouts, configure separate storage, model settings, index and session paths before starting the service; see [Operations](OPERATIONS.en.md).

## Start Unity

1. Open `UnityClient` in Unity Hub and let it restore packages, compile scripts and import shaders and assets.
2. Play `Assets/Scenes/TitleScene.unity`; the active game scene is CafeDemoV2.
3. Open **Settings → AI Configuration** and configure a Main provider, model, service URL and API key where required.
4. Save and apply. Restart your development Backend if the settings page indicates a restart is needed.
5. Enter the café and interact with the NPCs.

New Main profiles use a **32K-token request budget** by default; adjust it to your model's actual context window. The default built-in Embedding needs the resources described below. Alternatively, configure an external Embedding provider or turn off vector retrieval.

## First Unity import

The Editor checks six café models (Vivi, Ao, 桜田史利矢, GirlA, CozyCafe and Xingmang). After dependencies load, its import helper can repair initial UniVRM shader references. Successful validation is reported in Console or Editor.log:

```text
PUBLIC_PREVIEW_MODELS_VALIDATED: six models, five bound Humanoid avatars; meshes/materials/shaders valid (read-only)
PUBLIC_SOURCE_MODEL_IMPORT_READY: repaired=6; no source assets saved
```

The `repaired` count depends on the import state and can be zero. If a model remains incomplete, check package download, shader import and the asset path in the Editor log. For an explicit batch validation:

```text
-batchmode -quit -executeMethod PublicSourceModelImport.ValidateBatch
```

## Bundled Embedding resources

The source repository does not store the default Embedding weights. The fixed manifest is [builtin_embedding_resources.json](../AIBackend/app/builtin_embedding_resources.json), based on [onnx-community/embeddinggemma-300m-ONNX](https://huggingface.co/onnx-community/embeddinggemma-300m-ONNX/tree/5090578d9565bb06545b4552f76e6bc2c93e4a66), revision `5090578d9565bb06545b4552f76e6bc2c93e4a66`.

Place the eight manifest-listed files in `AIBackend/models/embeddinggemma-300m-q4/`, preserving the `onnx/` subdirectory:

- `onnx/model_q4.onnx` and `onnx/model_q4.onnx_data`
- `tokenizer.json`, `tokenizer_config.json`, `special_tokens_map.json`, `tokenizer.model`
- `config.json`, `README.md`

Set `EMBODIEDNPC_BUILTIN_EMBEDDING_DIR` to an absolute model directory if you keep the files elsewhere. The model is covered by its own [Gemma terms](https://ai.google.dev/gemma/terms), separately from this project's Apache-2.0 license.

Verify the eight SHA256 hashes from the `AIBackend` directory:

```powershell
@'
import hashlib, json, os
from pathlib import Path
spec = json.loads(Path('app/builtin_embedding_resources.json').read_text(encoding='utf-8'))
root = Path(os.environ.get('EMBODIEDNPC_BUILTIN_EMBEDDING_DIR', 'models/' + spec['model']))
for name, expected in spec['files'].items():
    with (root / name).open('rb') as source:
        actual = hashlib.file_digest(source, 'sha256').hexdigest()
    if actual != expected:
        raise SystemExit('SHA256 mismatch: ' + name)
print('PASS: all 8 resource hashes match; inference NOT_RUN')
'@ | .\.venv\Scripts\python.exe -B -
```

Missing files or hash mismatches will be reported immediately. Windows preview packages already include these assets.

## Local sessions and configuration

The Backend generates local session credentials for protected HTTP and WebSocket operations. Unity Editor reads `AIBackend/data/knowledge_admin_session.json` from the same checkout; the portable launcher manages a separate UserData directory. With multiple checkouts or a custom port, match the Backend URL, session path and data directory to the same instance. Keep API keys and session data on your machine.

`/live` checks process liveness; `/health` and runtime-status check readiness. An existing model profile takes precedence over `EMBODIEDNPC_PROVIDER=mock`, so use [Operations](OPERATIONS.en.md) for isolated Mock tests. See [Backend](../AIBackend/README.en.md) for API details.
