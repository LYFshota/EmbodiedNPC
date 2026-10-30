# CafeDemoV2 — four-NPC Demo

Open `Assets/Scenes/CafeDemoV2.unity`, the current development and acceptance entry. The original `Assets/Scenes/CafeDemo.unity` is retained for legacy regressions. Current Build Settings enable TitleScene → CafeDemoV2. Windows candidates have been built, but a candidate and its historical tests do not certify the latest WIP or clean-machine acceptance. See the root README for current scope.

This is a local scene upgrade, not a new runtime framework. Gameplay services, identity, action contracts, existing Humanoid controllers, OrbitCamera, dialogue, and the real cup are reused. Only the visual models, presentation references, scene transforms, primitive colliders and new NavMesh data differ.

- Player: unchanged Xingmang, visual scale 1.00793052, gameplay height approximately 1.75 m.
- Merchant: Vivi / 林薇薇, visual scale 1.17369533, measured T-pose mesh height 1.65 m.
- Seated customers: GirlA / 沈知意 (1.34936678, mesh height 1.575 m), Ao / 温予安 (1.05945325, 1.65 m), 桜田 史利矢 / 江亦舟 (0.916917562, 1.75 m).
- Gameplay roots remain scale 1. Raw hair/accessory bounds are clearance measurements, not body height.
- Source furniture sizes remain unchanged; the architectural shell is expanded. Imported static meshes are locally derived with original UVs/materials. No GLB/VRM runtime loader or custom parser was added.
- V2 uses simple colliders, a freshly baked `CafeDemoV2NavMesh.asset`, and the existing OrbitCamera at 3.1 m / 18 degrees.
- Existing low-poly geometry and replaced visuals are retained inactive. The original Merchant/RegularCustomer identities remain; CompanionCustomer and QuietCustomer have independent identities. BackgroundNpc is explicitly inactive.

Full raw captures and local evidence remain excluded from Git. Public capability and validation boundaries are summarized in [the acceptance summary](../../../../Docs/Validation/CafeDemoV2_2026-10-10.md).

## Dependencies and source permissions

This scene references `Assets/CafeEnvironmentCompatibilityTest/CozyCafe.glb`, `GirlA.vrm`, `Assets/VRMCompatibilityTest/Xingmang.vrm`, and the three imported models under `Assets/CafeDemo/V2/Characters`. These source assets and their stable GUIDs accompany V2. UniGLTF and VRM 1.0 are pinned to official UniVRM 0.131.3, commit `9750dadc592f421c356bf0376c73e61abbe323e3`; no repository-external local package path is required. Model source and permission notes are in `Characters/SOURCE.md`.

Xingmang has an independent limited-use license: © 2026 LYFshota. Public access and use to build, run, study, test and research EmbodiedNPC are permitted with the license and source credits retained; independent use or separate model redistribution needs permission. The owner confirms that this public grant authorizes compliant users under the embedded ExplicitlyLicensedPerson flag. See `Assets/VRMCompatibilityTest/Xingmang.LICENSE.md`. It is not CC0 or Apache-2.0. The commission/buyout declaration has not been independently checked against the contract; this documentation update does not modify the binary.

On 2026-10-10 the project owner confirmed that all models except Xingmang are CC0 for this GitHub delivery. See `Characters/SOURCE.md`; original metadata and source records remain unchanged. The earlier local audit did not independently establish all publication pages, including the VaM-derived texture source chain. The owner confirmation and embedded permissions are separate evidence; this delivery does not claim an independent rights audit or change the Xingmang authorization.

Cafe source attribution (copied from the existing asset audit): **Wacky**, **Cozy morning in a cafe — 3D Editor Challenge**, **CC-BY-4.0**. Source: https://sketchfab.com/3d-models/cozy-morning-in-a-cafe-3d-editor-challenge-3e70ea39900848d0951e1b47954361eb . VRM permissions are separate; retain the source metadata.

V2 is the current Cafe entry after TitleScene. Dated layout and acceptance notes below are historical evidence; they do not imply every later WIP change has been verified in Windows Player. For source startup, dependencies and built-in model preparation, see [source setup](../../../../Docs/SOURCE_SETUP.md).

## Counter layout revision — 2026-10-05

The counter assembly, its props and pendant lamps move 1.77 m toward the right wall. The rear left wall is inset with a short return, leaving approximately 1.2 m of clear passage beside the counter. Customer tables, chairs and tabletop objects retain their positions and sizes. Matching primitive colliders and the existing V2 NavMesh asset were updated. The real cup and its child approach points, Merchant spawn and counter markers follow the counter.

Runtime evidence: Player CharacterController traversed the passage 2.9 m in both directions while grounded; an injected bounded bring_object proposal completed through the formal action runtime, delivered the cup, and Merchant returned home. This was offline scene validation, not a real-model test. The original 48-view camera sweep predates this revision; this revision has three reviewed runtime views. Evidence: `SceneResources/CafeDemoV2Audit/Evidence/CounterRevision`; images: `UnityClient/Captures/CafeDemoV2/CounterRevision`.

## Four NPC P1 — 2026-10-09

Three customers start seated at the existing Table1 A/B and Table2 C chairs. `CafeV2Seated.controller` uses a standalone Humanoid `Sitting_Idle_Loop`; its existing masked Dialogue/Gesture/Carry layers remain. `CafeSeatedFootPose` keeps model-specific feet on the floor through Animator IK. Actor roots have scale 1, root motion is off, and seated Facing visual yaw is zero; HeadGaze and Nod remain available. SeatAnchors are authoring references, not runtime seat ownership.

Merchant and RegularCustomer keep `cafe.merchant` and `cafe.regular_customer`. New IDs are `cafe.companion_customer` and `cafe.quiet_customer`; event receiver routing and player dialogue candidates include all four. Customer action capabilities are empty. Existing Settings discovers the four definitions and edits independent local overrides without schema or backend changes. A preserved legacy RegularCustomer override can still show “顾客” instead of the new default “沈知意”; restoring that NPC's defaults is an explicit user Settings action.

The initial 2026-10-09 P1 validation used an external Unity copy. Subsequent 2026-10-10 core acceptance exercised isolated CafeDemoV2 Play through the actual Editor. These historical runs and the Git delivery rechecks are distinguished in [the public summary](../../../../Docs/Validation/CafeDemoV2_2026-10-10.md); a standalone build is still not verified.

Continuous ambient conversation, P3 emotion expressions and P4 random idle are not implemented. `Sitting_Talking_Loop` is an imported compatibility resource only. This scene is already configured; the one-time `CafeV2NpcP1Setup` authoring menu is not needed again and must not be rerun over updated Persona/scene authoring.

## Memory, continuation and grounding — 2026-10-10

Fixed single-player UID is `cafe-local-player-001`. Four NPCs retain separate canonical Memory owners. Displayed NPC self-statements can form personal memories with an explicit self-report source limitation; they cannot prove Player/shared history or executed world actions. Real successful bring_object can form an executed Episodic and be recalled after a Backend restart.

DialogueContinuationScheduler is wired but disabled by default, waits 4 seconds and permits at most one continuation opportunity per Player input. It reuses the event/cognition pipeline; Player input cancels old continuations, and only successful displayed/ACKed responses become Experience. The opportunity can choose NO_ACTION.

Current observed seats and visible occupant identities use the observer's own perspective. Unknown is not empty; Persona familiarity is not proof of today's arrival/current position. Missing beside geometry remains an explicit limitation. Historical real acceptance and current delivery checks are separated in [the public validation summary](../../../../Docs/Validation/CafeDemoV2_2026-10-10.md).
