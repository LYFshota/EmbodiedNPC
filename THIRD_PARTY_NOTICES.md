# 许可范围与第三方资源

Copyright 2026 LYFshota

原创核心代码、原创文档和配置模板遵循 [Apache-2.0](LICENSE)。导入代码与素材不因位于仓库内就变为项目原创。完整默认 Demo 包含独立许可素材，不能统称为无限制商业使用的软件包。美术限制不修改 Apache-2.0 代码授权。

| 组件 | 实际许可及声明 |
|---|---|
| 项目有权许可的原创/生成美术 | 仅 [Project_Art 资源级清单](ThirdPartyNotices/Project_Art.md) 明确列入的资源按 CC0 1.0；木门与已由所有者确认的三个原创小资源均已列明，不扩大到第三方派生贴图。 |
| Vivi / 林薇薇、GirlA / 沈知意、桜田史利矢 / 江亦舟 | 沿用已经核实的原始 CC0 来源与格式转换记录；见 [角色来源](UnityClient/Assets/CafeDemo/V2/Characters/SOURCE.md)。不把游戏内身份当成作者背书。 |
| Ao / 温予安 | 白い白米 / siroihakumai；具体 VRoid Hub 模型 5095341574274335587，VRM Public License 1.0 与本文件内设置共同适用。允许原始/改作再分发及个人/公司商用；保留政治宗教、反社会/仇恨表达限制。不是 CC0。页面来源已确认，精确发布修订对应缺口仍记录于角色来源。 |
| Xingmang / 星芒 | 已正式采用[独立项目内非商业许可](UnityClient/Assets/VRMCompatibilityTest/Xingmang.LICENSE.md)：允许 EmbodiedNPC 及 Fork 内非商业使用、修改、改作及随源码/构建分发原模型或修改版，禁止未经单独授权商用、单独销售或独立素材包分发；保留辉歪歪署名及现有暴力/性用途限制。OWNER CONFIRMED；独立部件例外及商业标志 Disallow 见[资产清单](ThirdPartyNotices/Xingmang_Assets.md)，不限制 Apache-2.0 代码商用。 |
| Cafe / CozyCafe 及派生纹理 | fangzhangmnm 原作、Wacky 改作贡献 CC BY 4.0；VamTastic 的 Anime Cafe 补片贡献来源 CC BY 已确认，具体版本未核实。各贡献分开记录，见 [来源与修改说明](ThirdPartyNotices/Cafe_Attribution.md)，不能把上游 4.0 自动套到 VaM 贡献。 |
| KayKit / Quaternius 动画 | 保留各自 CC0 来源：[KayKit](UnityClient/Assets/CafeDemo/Animations/ThirdParty/KayKit/SOURCE.md)、[Quaternius](UnityClient/Assets/CafeDemo/Animations/ThirdParty/Quaternius/SOURCE.md) 及随附许可。站立姿态沿用已验收的派生 Idle；对话复用既有 Quaternius Idle_Talking_Loop / Sitting_Talking_Loop，并由项目表现层控制节奏和幅度。原始动画授权及来源继续保留；四种情绪图标使用现有 Noto 字体，没有新增外部图标素材。 |
| Kenney Food Kit 2.0 / Furniture Kit 2.0 | 保留 [Kenney 原许可](ThirdPartyNotices/Kenney)，CC0；不由项目美术声明二次授权。 |
| Coffee House Bump — OatCog；little_cafe.mp3 | [来源及 CC0 1.0](UnityClient/Assets/CafeDemo/Audio/BGM/SOURCE_AND_LICENSE.md)。项目方由原 WAV 转 MP3；自愿署名不新增 CC0 条件。 |
| Noto Sans CJK | [SIL OFL 1.1](UnityClient/Assets/Fonts/NotoSansCJKsc/LICENSE-OFL-1.1.txt)，源 OTF、TMP 与许可保留；微软雅黑已排除。 |
| Unity、UniVRM/UniGLTF、NativeWebSocket 及其他 UPM 包 | 依 [manifest](UnityClient/Packages/manifest.json) 和 [lock](UnityClient/Packages/packages-lock.json) 自动恢复，按各包原许可。包缓存和 Editor 本体不在源码树中；包源码不在本树；发行包保留实际恢复版本的许可和声明，包元数据字段不替代其许可正文。 |
| Python、第三方包及原生库 | 由 [requirements](AIBackend/requirements.txt) 安装，各发行包保留原许可；[依赖快照](Docs/DEPENDENCIES.md) 不是对所有依赖重新许可。虚拟环境及 DLL 不随源码复制。 |
| EmbeddingGemma 权重、tokenizer 和 ONNX 转换 | 不随源码提供；由开发者依 [固定资源说明](Docs/SOURCE_SETUP.md#内置-embedding-资源) 合法准备，或选外部 Embedding/关闭。模型条款不改为 Apache-2.0。 |

OlyPoly 原资源、微软雅黑、三个 Mixamo 原始 FBX 以及隐藏立绘的清理结果已沿用；本准备没有再引入它们。历史私有 ZIP 不是本公开候选，不能替代此源码清单上传。

许可与版本证据边界见 [发布状态](Docs/RELEASE_STATUS.md)。VamTastic 的 CC BY 具体版本未注明，保留这一事实、实际作者和修改记录，不伪造版本，也不扩大任何作者的授权范围。Unity 项目模板保留[原许可声明](ThirdPartyNotices/UnityTemplate_LICENSE.md)。

## Windows preview: bundled Embedding

The Windows preview bundles EmbeddingGemma-300M Q4 ONNX (Google DeepMind; ONNX/Q4 distribution by onnx-community). Source Git does not contain the weights. The runtime model directory includes the Gemma Terms of Use, Prohibited Use Policy, binding model LICENSE, Notice and CONVERSION_AND_SOURCE.md; these govern the model independently of Apache-2.0. The original pinned model README is preserved.
