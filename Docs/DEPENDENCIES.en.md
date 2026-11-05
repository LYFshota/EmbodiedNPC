[简体中文](DEPENDENCIES.md) | [English](DEPENDENCIES.en.md)

# Dependencies and development environment

The project uses **Unity 6000.5.10f1** and **Python 3.12 x64** on Windows. See [Source Setup](SOURCE_SETUP.en.md) for step-by-step instructions.

## Python Backend

The dependency specification is [requirements.txt](../AIBackend/requirements.txt). Install in a new virtual environment:

```powershell
cd AIBackend
py -3.12 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
.\.venv\Scripts\python.exe -m pip check
```

Major components include FastAPI, Uvicorn, HTTPX, Pydantic, FastEmbed and ONNX Runtime. Installed versions follow the dependency resolver and current requirements.

## Unity

The Unity package set is defined by [manifest.json](../UnityClient/Packages/manifest.json) and [packages-lock.json](../UnityClient/Packages/packages-lock.json). It includes URP, Input System, AI Navigation, Test Framework and UniVRM.

Unity restores the pinned project dependencies when first opened.

## Model assets

Windows preview packages include default Embedding assets. Source installations prepare these separately following [Embedding setup](SOURCE_SETUP.en.md#bundled-embedding-resources). The Main LLM is configured by the user.

For third-party component terms and attribution, see [THIRD_PARTY_NOTICES](../THIRD_PARTY_NOTICES.en.md).
