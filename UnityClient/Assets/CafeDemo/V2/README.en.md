[简体中文](README.md) | [English](README.en.md)

# CafeDemoV2

`Assets/Scenes/CafeDemoV2.unity` is the active café gameplay scene. The main startup entry is `Assets/Scenes/TitleScene.unity`.

## Scene and NPCs

The café contains a service counter, customer tables, service zones and a NavMesh. Xingmang is the player character. The four NPCs are:

- **Vivi / 林薇薇:** barista, authorized for drink preparation, object delivery and snack service.
- **Shen Zhiyi / 沈知意:** café customer.
- **Wen Yu'an / 温予安:** café customer.
- **Jiang Yizhou / 江亦舟:** café customer.

Each NPC has its own identity, persona, perception, cognition and memory. The scene reuses event routing, daily modes, Mini-GOAP, action execution and relationship systems. Some ambient activity runs locally in Unity.

## Development

Open `UnityClient` in Unity Hub and Play TitleScene. The runtime uses the existing NavMesh, CharacterController, NavMeshAgent and character animation components.

See [Source Setup](../../../../Docs/SOURCE_SETUP.en.md) for dependencies and startup and [Architecture](../../../../Docs/ARCHITECTURE.en.md) for data flow.

## Asset sources

The scene includes CozyCafe, VRM characters and third-party animation assets. See [Character Sources](Characters/SOURCE.en.md) and [Third-party Notices](../../../../THIRD_PARTY_NOTICES.en.md). Xingmang uses a [separate character license](../../VRMCompatibilityTest/Xingmang.LICENSE.md), distinct from the Apache-2.0 license for original code.
