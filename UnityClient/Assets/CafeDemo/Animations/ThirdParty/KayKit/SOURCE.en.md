[简体中文](SOURCE.md) | [English](SOURCE.en.md)

# KayKit animation provenance and adaptations

EmbodiedNPC uses **KayKit: Character Animations 1.1** by **Kay Lousberg**, under the supplied [CC0 license](LICENSE.txt).

Sources: [Author website](https://www.kaylousberg.com/) · [KayKit releases](https://kaylousberg.itch.io/).

## Clips used in this project

| Clip | Source | Purpose |
| --- | --- | --- |
| `Waving.anim` | Medium Simulation / Waving | Player and NPC waving |
| `Cheering.anim`, `Flexing.anim` | Medium/Large Simulation | Player emote animations |
| `Holding_B.anim` | Medium Tools / Holding_B | Cup carry upper-body pose |
| `Idle_A.anim`, `Idle_Relaxed.anim` | Medium General / Idle_A | Relaxed standing and dialogue accents |
| `Idle_Quiet.anim`, `Idle_Quiet_Vivi.anim` | Adapted from Idle_Relaxed | Quieter default posture for Xingmang and Vivi |
| `Waving_PlayerRelaxed.anim` | Adapted from Waving | More relaxed player wave |

Unity imports the source as Humanoid and extracts independent `.anim` clips. Runtime reuses existing Animators, masks and character Humanoid Avatars, while game movement and root motion remain controlled by their original systems.

## Adaptations

- **Waving:** Original arm motion under the upper-body mask while head and movement remain separately controlled.
- **Holding:** `Holding_B` is selected for its compact lower-arm posture; actual cup ownership is tracked by game systems.
- **Idle:** `Idle_Quiet` variants reduce unnecessary motion around a relaxed VRM pose.
- **Emotes:** Cheering and Flexing are presentations rather than new world-action permissions.

Original notices and relevant derivative records remain alongside the clips. For other sourced motion, see [Quaternius](../Quaternius/SOURCE.en.md).

[Third-party Notices](../../../../../../THIRD_PARTY_NOTICES.en.md)
