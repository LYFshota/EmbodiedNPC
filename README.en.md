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

<p align="center">
  <img src="Docs/Images/cafe-panorama.jpg" alt="EmbodiedNPC cafe panorama" width="640">
</p>

## ✨ What works today

- **💬 Independent character conversations:** The café has four NPCs with distinct personas, personalities and dialogue styles. They support Chinese and English conversations and can naturally continue a topic based on context.
- **🧠 Long-term memory and experience processing:** NPCs can record actual conversations and experiences and process them into long-term memories in the background. Dialogue combines world-knowledge retrieval with on-demand memory recall, allowing characters to gradually get to know the player.
- **🌍 World knowledge:** Includes 108 entries covering coffee, characters and the neighborhood. Players can search, edit, add, import and export knowledge, and configure which NPCs know it and how it is used.
- **👂 Limited perception and overhearing:** NPCs perceive local scene information and can record nearby text speech when range and occlusion conditions permit, without automatically interrupting.
- **☕ Scene actions and feedback:** NPCs with the appropriate capabilities can deliver items, prepare ten hot drinks and clean counters. The game validates and executes actions, then feeds actual results back into cognition and memory.
- **📅 Persistent tasks and meeting arrangements:** NPCs can save a limited set of future requests to consider at the next meeting or another suitable opportunity, with support for deferral and cancellation.
- **✨ Expressions and dialogue motion:** NPCs have varied dialogue motions, facial expressions and conversational rhythms, complemented by occasional happy, confused, surprised and thinking icons.
- **⚙️ Model configuration:** The main model and vector-retrieval model can be configured separately, using local models or remote services.
- **📦 Complete experience data export:** Players can export conversations, long-term memories, personas, world knowledge, relationships, tasks and other data from Settings for personal records or developer feedback. Exports may contain personal information: review them before sharing. The game does not upload them automatically.

## Current limitations

Expression capabilities and visual results may vary between VRM models.

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

[Download the Windows x64 preview ZIP](https://github.com/LYFshota/EmbodiedNPC/releases/download/v0.1.3-preview/EmbodiedNPC-Windows-x64-v0.1.3-preview.zip) · [Pre-release notes](https://github.com/LYFshota/EmbodiedNPC/releases/tag/v0.1.3-preview). Extract the complete archive and run `Start.cmd`. No Unity Editor or separate Python installation is needed. Personal settings and memories persist in the package-local `UserData`; shut down normally and back it up before updating. It bundles EmbeddingGemma-300M Q4 ONNX for memory vectors, so no extra Embedding download or API setup is normally needed. You must still configure your own Main LLM for dialogue and decisions. External Embedding and disabled-vector options remain available.

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
