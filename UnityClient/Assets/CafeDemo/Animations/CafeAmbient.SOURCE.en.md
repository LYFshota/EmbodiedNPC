[简体中文](CafeAmbient.SOURCE.md) | [English](CafeAmbient.SOURCE.en.md)

# Café ambient animation sources and implementation

These café ambient animations were baked as Humanoid motion curves with project tools such as `CafeAmbientSetup`. They reuse existing motion channels, navigation and admission components. Unity schedules these lightweight presentations locally.

## Animation clips

| Clip | Purpose |
| --- | --- |
| `CafeSeatedDrink.anim` | Seated drinking motion for Wen Yu'an and Jiang Yizhou |
| `CafeAmbientWipe.anim` | Light counter-wiping presentation for Vivi |
| `CafeServeSnack.anim` | Snack-service reach, hold and retract |

### Seated drinking

`CafeSeatedDrink.anim` lasts about **6.2 seconds** and animates the right arm and fingers. Contact is reached at about 1.1 s, drinking runs through 2.4–4.1 s, and the hand releases near 5.1 s before returning to seated idle.

The decorative cup follows a hand anchor and returns to its saved scene transform on completion or cancellation. The seated base, head, body, root, legs and foot IK remain under existing animation control. Wen and Jiang have separate anchors and timing; Shen's drinking animation is currently disabled.

### Counter wiping

`CafeAmbientWipe.anim` builds on the project's existing cleaning-arm presentation, with finger contact adjusted for Vivi. Ambient wiping is visual behavior; the formal `CleanCounter` action uses its own execution and world-state settlement. Dialogue, formal tasks and emergencies can interrupt the ambient motion.

### Snack service

`CafeServeSnack.anim` provides a project-authored reach, hold and retract animation. The snack service uses a logical result rather than a spawned food prop; outcome authority stays with the executor.

## Extension and licensing

These clips reference existing licensed [Quaternius](ThirdParty/Quaternius/SOURCE.en.md) and [KayKit](ThirdParty/KayKit/SOURCE.en.md) animations. New baked curves are authored within the project. New seated characters require their own table and hand anchors, contact calibration and timing.

[Architecture](../../../../Docs/ARCHITECTURE.en.md) · [Asset Licenses](../../../../THIRD_PARTY_NOTICES.en.md)
