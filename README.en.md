[简体中文](README.md) | English

# ☕ EmbodiedNPC

**An experimental multi-NPC cognition and interaction project for game scenes**

🚧 Early Prototype · Under active development

Unity 6 · Python / FastAPI · Long-term memory · Local / remote LLMs

EmbodiedNPC is a personal research project built with Unity and Python/FastAPI.
The current demo is a café with four independent NPCs that can talk to the player
and perform a limited set of actions. It explores how perception, memory,
decisions and actions can connect a character's responses to its experiences
and immediate surroundings. It is an early prototype, not a mature general-purpose NPC framework.

## What works today

- **Independent character conversations:** four NPCs have distinct personas,
  dialogue styles and memories, with Chinese and English responses.
- **Long-term memory:** eligible displayed dialogue and actual action results
  can become experiences, which background processing turns into memories for later recall.
- **Limited perception and overhearing:** NPCs receive local scene information
  and can record nearby text speech subject to range and occlusion. Overhearing
  does not automatically trigger an interruption or a free-form group conversation.
- **Scene actions and feedback:** capable characters can deliver cups, prepare
  drinks and clean counters. Unity validates and executes actions and returns actual results.
- **Persistent tasks and meeting arrangements:** limited requests can persist
  until a later visit or idle opportunity, be deferred or cancelled. A verbal promise
  does not mean a task was saved, and a saved task is not guaranteed to run immediately.
- **Model configuration:** Main and Embedding can be configured separately,
  using local or remote services.

## Current limitations

The implementation is closely tied to the fixed café demo. NPC autonomy and the
available actions are limited. Models can misunderstand, repeat questions or
produce unnatural or unsupported replies; memory processing and retrieval can also fail.

The underlying architecture and interaction experience are being actively improved.
Many features are incomplete, and some design decisions may change as research continues.

## Directions to explore

- **More natural multi-party conversations:** players and NPCs sharing a scene,
  proactive speech and responses, and overlapping participation.
- **More persistent cognition and social relationships:** social memory,
  relationships and longer-term goals shaped by experience and context.
- **Richer autonomous world interactions:** additional actions, ongoing tasks
  and cooperation that can affect the world and create emergent stories.
- **A more extensible development foundation:** less dependence on the café,
  easier character creation, cognition/memory configuration, actions and new Unity scenes.
- **Model and runtime efficiency:** model compatibility, inference cost and
  concurrent NPC processing on limited hardware.

These are research directions, not committed features or a release schedule.

## Download and try

[Download the Windows x64 preview ZIP](https://github.com/LYFshota/EmbodiedNPC/releases/download/v0.1.0-preview/EmbodiedNPC-Windows-x64-v0.1.0-preview.zip) · [Pre-release notes](https://github.com/LYFshota/EmbodiedNPC/releases/tag/v0.1.0-preview). Extract the complete archive and run `Start.cmd`. It bundles EmbeddingGemma-300M Q4 ONNX for memory vectors, so no extra Embedding download or API setup is normally needed. You must still configure your own Main LLM for dialogue and decisions. External Embedding and disabled-vector options remain available.

## Run from source

The currently supported development path is Windows with **Unity 6000.5.10f1**,
**Python 3.12 x64**, Git and a Main model service that you configure yourself.
Main model weights are not included. Built-in Embedding weights are also not
included: prepare them as documented, select external Embedding, or disable it
before saving the first model configuration.

1. From the repository root, install dependencies and start the Backend:

   ```powershell
   cd .\AIBackend
   py -3.12 -m venv .venv
   .\.venv\Scripts\python.exe -m pip install -r requirements.txt
   .\.venv\Scripts\python.exe -m uvicorn app.main:app --host 127.0.0.1 --port 8000 --no-proxy-headers
   ```

2. Open `UnityClient` in Unity Hub. Let packages, scripts and shaders finish importing,
   then run `Assets/Scenes/TitleScene.unity` and enter the café from the title screen.
   First import may require Reimport on the six existing VRM/GLB assets listed in
   [source setup](Docs/SOURCE_SETUP.md); do not regenerate scenes or copy someone else's Library.
3. Configure Main and Embedding, save/apply, and restart this checkout's Backend when prompted.

See [source setup](Docs/SOURCE_SETUP.md) for dependency/model preparation and
[architecture](Docs/ARCHITECTURE.md) for the main boundaries. Some detailed developer documents are currently in Chinese.

## Feedback and contributions

If setup or gameplay fails, you find a bug, or you have suggestions about the
architecture, features or future directions, please use
[GitHub Issues](https://github.com/LYFshota/EmbodiedNPC/issues), submit a Pull Request,
or email [952650555@qq.com](mailto:952650555@qq.com).

Small fixes, documentation and reproducible bug reports are welcome. Read the
[contribution guide](CONTRIBUTING.md), describe what you actually validated, and
share only redacted logs. Do not upload API keys, private conversations or save databases.

## Code and asset licenses

Original code, documentation and configuration templates use **Apache-2.0**.
Project-owned art expressly listed in the [asset schedule](ThirdPartyNotices/Project_Art.md)
uses CC0 1.0. Third-party code, characters, models, animations, fonts and audio
retain their own licenses; the complete default demo is not unrestricted commercial content.

Xingmang uses an adopted [project-scoped non-commercial license](UnityClient/Assets/VRMCompatibilityTest/Xingmang.LICENSE.md):
non-commercial use, modification and redistribution with EmbodiedNPC/forks and
their builds are permitted. Commercial use requires separate authorization;
standalone sale and asset-pack distribution are not granted. Preserve the creator
credit and existing use restrictions. This asset license does not limit commercial
use of the Apache-2.0 original code.

See [LICENSE](LICENSE), [NOTICE](NOTICE) and [THIRD_PARTY_NOTICES](THIRD_PARTY_NOTICES.md).
