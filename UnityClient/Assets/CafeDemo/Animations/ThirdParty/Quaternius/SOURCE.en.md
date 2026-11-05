[简体中文](SOURCE.md) | [English](SOURCE.en.md)

# Quaternius Humanoid animation sources

EmbodiedNPC uses clips from **Quaternius · Universal Animation Library (Standard)** under its [CC0 1.0 license](LICENSE.txt).

Sources: [Official site](https://quaternius.com/packs/universalanimationlibrary.html) · [Release page](https://quaternius.itch.io/universal-animation-library).

Original local FBX: `SceneResources/Universal Animation Library[Standard]/Unity/UAL1_Standard.fbx`, SHA-256:

```text
21B32D912DA3CB93426D974FB945E86F5B2E86970ACD2CE89905E0FBF9F1DCC2
```

## Clips used

| Standalone clip | Purpose |
| --- | --- |
| `Walk_Loop.anim` | Locomotion for standing characters |
| `Idle_Loop.anim` | Standing idle |
| `Idle_Talking_Loop.anim` | Lightweight upper-body dialogue accents |
| `Sitting_Idle_Loop.anim` | Seated customer base animation |
| `Sitting_Talking_Loop.anim` | Seated dialogue compatibility clip |

Unity imports the source as Humanoid and extracts standalone `.anim` assets. The runtime uses those extracted clips without depending on the entire source FBX.

## Playback

The existing `NavMeshAgent` and locomotion presentation manage actual movement. Root Motion is disabled, keeping Unity's world state authoritative for actor positions.

Dialogue motion uses an upper-body AvatarMask, leaving root, legs and HeadGaze to their existing controllers. Seated customers retain foot IK for contact with chairs and the ground.

Original attribution and licensing are retained in `LICENSE.txt`. Related waving and carrying poses come from [KayKit](../KayKit/SOURCE.en.md).

[Third-party Notices](../../../../../../THIRD_PARTY_NOTICES.en.md)
