简体中文 | [English](README.en.md)

# ☕ EmbodiedNPC

**面向游戏场景的多 NPC 认知与交互系统**

🚧 Early Prototype · 持续开发中

Unity 6 · Python / FastAPI · 长期记忆 · 本地 / 远程 LLM

这是我用 Unity 和 Python / FastAPI 持续开发的一个实验项目。目前的演示场景是一间咖啡馆，里面有四名独立 NPC，可以与玩家交谈，并在有限的场景范围内做出行动。

我希望探索怎样把感知、记忆、决策和实际行动连接起来，让角色的回应与它们经历过的事情、眼前的环境产生联系。项目还在早期原型阶段，架构、交互和使用体验都有不少需要完善的地方，欢迎感兴趣的开发者下载研究、提出建议，或一起参与改进。



![EmbodiedNPC 咖啡馆全景](Docs/Images/cafe-panorama.jpg)

## ✨ 目前实现了什么？

- **💬 多角色独立对话**：四名 NPC 各有角色设定、对话风格和个人记忆，支持中文、英文回复。
- **🧠 长期记忆与经历整理**：有效对话和实际行动结果可以积累为经历，在后台整理成长期记忆，供之后交流时检索使用。
- **👂 有限感知与旁听**：角色接收局部场景信息，附近的 NPC 可在范围和遮挡限制下记录第三方的文字发言；旁听不会自动触发插话，也不等于完整的自主群聊。
- **☕ 场景动作与反馈**：具备相应能力的角色可以递杯、准备饮品、清理台面等。动作由游戏侧检查和执行，实际结果会反馈给系统。
- **📅 持久任务与见面约定**：支持保存有限的委托，在下次来访或空闲时考虑执行，也可以延期或取消；口头答应不等于已保存任务，保存也不保证立即执行。
- **⚙️ 模型配置**：主模型与 Embedding 可以分别配置，支持接入本地或远程模型服务。

## 🚧 当前还有哪些不足？

目前的实现主要围绕固定的咖啡馆 Demo 展开，对场景的依赖较强，NPC 的自主性和行为丰富度也比较有限。

模型仍可能理解偏差、重复提问，或给出不够自然、缺乏依据的回复，记忆整理与召回也可能出错。

项目目前正在持续进行底层架构和交互体验优化。现阶段仍有不少功能不够完善，部分设计也可能随着后续研究发生调整。

## 🌱 接下来想探索的方向

- **💬 更自然的多角色交流**：探索多个玩家与 NPC 在同一场景中自由交流，让角色能够主动发言、互相回应，并处理多方同时参与的交互。
- **🧠 更持续的角色认知与社会关系**：尝试让 NPC 保留更持续的社会记忆，发展角色之间的关系和长期目标，并结合经历与当下情境选择社交和行为。
- **🌍 更丰富的自主行为与世界互动**：扩展行动、持续任务和角色协作，探索这些行为如何影响游戏世界，以及从互动中逐渐形成的故事。
- **🛠️ 更易扩展的开发框架**：逐步减少对咖啡馆场景的依赖，让开发者更方便地创建角色、配置认知与记忆、扩展行为，并接入自己的 Unity 场景。
- **⚡ 模型与运行效率优化**：研究不同模型的适配、推理开销和多 NPC 并行运行，尝试在有限硬件下保持合理的交互体验。

以上只是目前正在探索的方向，具体实现方式和优先级可能调整，也暂时没有固定的完成时间。如果你对其中某个方向有经验或想法，欢迎通过 Issue 一起讨论。

## 下载试玩

[下载 Windows x64 试玩 ZIP](https://github.com/LYFshota/EmbodiedNPC/releases/download/v0.1.0-preview/EmbodiedNPC-Windows-x64-v0.1.0-preview.zip) · [Pre-release 说明](https://github.com/LYFshota/EmbodiedNPC/releases/tag/v0.1.0-preview)。完整解压后运行 `Start.cmd`。试玩包内置 EmbeddingGemma-300M Q4 ONNX，用于记忆向量，不需要额外下载或配置 Embedding API；主模型（Main LLM，用于对话与决策）仍需自行配置。外部 Embedding 和关闭向量能力选项继续保留。

## 🚀 如何从源码运行？

当前主要面向 Windows 本机开发，需要 **Unity 6000.5.10f1**、**Python 3.12 x64**，以及自行准备的可用主模型服务。

> **💡 运行前请注意**
>
> - 项目不包含主模型，需要自行配置可用的本地或远程 LLM 服务。
> - 内置 Embedding 权重不随源码提供，可按文档准备，也可在首次保存配置前选择外部 Embedding 或关闭向量能力。
> - Unity 首次导入时，部分 VRM/GLB 资源可能需要定向 Reimport，具体见 [源码运行说明](Docs/SOURCE_SETUP.md)。

1. 从仓库根目录打开 PowerShell，安装依赖并启动 Backend：

   ```powershell
   cd .\AIBackend
   py -3.12 -m venv .venv
   .\.venv\Scripts\python.exe -m pip install -r requirements.txt
   .\.venv\Scripts\python.exe -m uvicorn app.main:app --host 127.0.0.1 --port 8000 --no-proxy-headers
   ```

2. 用 Unity Hub 打开 `UnityClient`，等待包和资源导入完成，运行 `Assets/Scenes/TitleScene.unity`，从标题页进入咖啡馆。
3. 按提示配置主模型和 Embedding，保存后按提示重启 Backend，使配置生效。

完整的环境安装、模型资源准备和首次导入处理见 [源码运行说明](Docs/SOURCE_SETUP.md)；架构介绍见 [Docs](Docs/ARCHITECTURE.md)。

## 🤝 交流与参与

如果你在体验或部署过程中遇到问题、发现 Bug，或者对项目架构、功能和未来开发方向有建议，欢迎通过 [GitHub Issues](https://github.com/LYFshota/EmbodiedNPC/issues)、Pull Requests 或电子邮件 [952650555@qq.com](mailto:952650555@qq.com) 交流。

不必从大功能开始，修复一个小问题、补充一段说明，同样很有帮助。提交前可以看看 [贡献指南](CONTRIBUTING.md)；反馈问题时附上复现步骤和脱敏日志，请不要上传 API Key、私人对话或存档数据库。

## 📄 开源与许可

原创核心代码、原创文档和配置模板采用 **Apache-2.0**。第三方代码、美术、角色、动画、字体和音频遵循各自许可，完整 Unity 资源不能一概视为可自由商用。

项目方有权授权的原创美术按 [资源级清单](ThirdPartyNotices/Project_Art.md) 采用 CC0 1.0；不覆盖第三方派生资源。星芒按[独立项目内非商业许可](UnityClient/Assets/VRMCompatibilityTest/Xingmang.LICENSE.md)授权，允许在 EmbodiedNPC 及其 Fork 内非商业使用、修改、改作，并随源码或游戏构建分发原始模型或修改版；未经单独授权不得商用，也不得单独销售或作为独立素材包分发。该素材许可不限制原创 Apache-2.0 代码商用，辉歪歪署名和现有用途限制保留，具体资产与元数据见[资产清单](ThirdPartyNotices/Xingmang_Assets.md)。

详细信息请参阅 [LICENSE](LICENSE)、[NOTICE](NOTICE) 和 [THIRD_PARTY_NOTICES](THIRD_PARTY_NOTICES.md)。
