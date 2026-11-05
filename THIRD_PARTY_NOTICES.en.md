[简体中文](THIRD_PARTY_NOTICES.md) | [English](THIRD_PARTY_NOTICES.en.md)

# Third-party assets and licenses

Copyright 2026 LYFshota.

EmbodiedNPC's original code and documentation are released under [Apache-2.0](LICENSE). Game assets have separate rights and provenance; use the license applicable to each resource.

| Component | License and source |
| --- | --- |
| Selected first-party art | Explicitly listed original/generated assets are dedicated under CC0 1.0 in the [art schedule](ThirdPartyNotices/Project_Art.en.md). |
| Vivi, GirlA and 桜田史利矢 | CC0 source models with attribution and conversion details in [character sources](UnityClient/Assets/CafeDemo/V2/Characters/SOURCE.en.md). |
| Ao / 温予安 | By 白い白米 (siroihakumai), [VRoid Hub model](https://hub.vroid.com/en/characters/1245908975744054638/models/5095341574274335587), governed by VRM Public License 1.0 and its model-specific settings. This is distinct from CC0. |
| Xingmang / 星芒 | Separate [project-scoped non-commercial license](UnityClient/Assets/VRMCompatibilityTest/Xingmang.LICENSE.md). Modeling credit: 辉歪歪; see the [asset schedule](ThirdPartyNotices/Xingmang_Assets.en.md). The code's Apache-2.0 license remains independent. |
| CozyCafe scene and textures | fangzhangmnm source and Wacky derivative under CC BY 4.0; a VamTastic texture contribution retains its separately recorded CC BY terms. Full attribution: [Cafe](ThirdPartyNotices/Cafe_Attribution.en.md). |
| KayKit and Quaternius animations | CC0 source packages with notices: [KayKit](UnityClient/Assets/CafeDemo/Animations/ThirdParty/KayKit/SOURCE.en.md) and [Quaternius](UnityClient/Assets/CafeDemo/Animations/ThirdParty/Quaternius/SOURCE.en.md). |
| Kenney Food Kit 2.0 and Furniture Kit 2.0 | CC0; original licenses in [Kenney notices](ThirdPartyNotices/Kenney). |
| Coffee House Bump | OatCog, CC0 1.0; [music source and conversion](UnityClient/Assets/CafeDemo/Audio/BGM/SOURCE_AND_LICENSE.en.md). |
| Noto Sans CJK | SIL OFL 1.1; license in `UnityClient/Assets/Fonts/NotoSansCJKsc/LICENSE-OFL-1.1.txt`. |
| Unity / UniVRM / NativeWebSocket | Respective package licenses and source metadata; see the Unity package manifest/lock and [NativeWebSocket](ThirdPartyNotices/NativeWebSocket_SOURCE.en.md). |
| Python dependencies | Licenses distributed with individual Python packages, installed from `AIBackend/requirements.txt`. |
| EmbeddingGemma-300M Q4 ONNX | Model and conversion terms apply independently of Apache-2.0; the Windows preview carries the model's license, Notice and applicable policies. Source repositories prepare weights separately. |

The [Unity template](ThirdPartyNotices/UnityTemplate_SOURCE.en.md) retains its own Companion License notice. Original license texts, author names and applicable credits are maintained in their respective source files.

## Windows preview and embedding

The Windows preview bundles EmbeddingGemma-300M Q4 ONNX from Google DeepMind and onnx-community. Its runtime resource directory includes the Gemma Terms of Use, Prohibited Use Policy, model license, Notice and conversion/source record. The model's rights and restrictions are separate from those of EmbodiedNPC's original code.

See [source setup](Docs/SOURCE_SETUP.en.md) for preparing a local source installation.
