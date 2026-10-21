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

Full raw captures and local evidence remain excluded from Git. Public capability and validation boundaries are summarized in [the acceptance summary](../../../../Docs/RELEASE_STATUS.md).

## Dependencies and source permissions

The complete café, four NPCs and Xingmang remain included, with their stable model GUIDs. UniGLTF/VRM10 are pinned in the package lock. Character-specific sources and restrictions are recorded in [Characters/SOURCE.md](Characters/SOURCE.md); Ao is not CC0. Xingmang owner rights are OWNER CONFIRMED and its project-scoped non-commercial license has been formally adopted.

See [resource licenses](../../../../THIRD_PARTY_NOTICES.md) and [Cafe attribution and modifications](../../../../ThirdPartyNotices/Cafe_Attribution.md). fangzhangmnm/Wacky contributions use CC BY 4.0; the VamTastic contribution is marked CC BY with its version unverified. The hidden standee and its GLB-exclusive data were removed in the prior candidate; the current café is a modified derivative, not the untouched source file. Historical evidence and private reports are not distributed in this source tree.

Run the existing TitleScene → CafeDemoV2 flow and follow [source setup](../../../../Docs/SOURCE_SETUP.md), including six-resource Reimport after package/shader import if needed. Do not rerun one-time scene authoring menus.

## Counter layout revision — 2026-10-05

The counter assembly, its props and pendant lamps move 1.77 m toward the right wall. The rear left wall is inset with a short return, leaving approximately 1.2 m of clear passage beside the counter. Customer tables, chairs and tabletop objects retain their positions and sizes. Matching primitive colliders and the existing V2 NavMesh asset were updated. The real cup and its child approach points, Merchant spawn and counter markers follow the counter.

Runtime evidence: Player CharacterController traversed the passage 2.9 m in both directions while grounded; an injected bounded bring_object proposal completed through the formal action runtime, delivered the cup, and Merchant returned home. This was offline scene validation, not a real-model test. The original 48-view camera sweep predates this revision; this revision has three reviewed runtime views. Evidence: `SceneResources/CafeDemoV2Audit/Evidence/CounterRevision`; images: `UnityClient/Captures/CafeDemoV2/CounterRevision`.

## Four NPC P1 — 2026-10-09

Three customers start seated at the existing Table1 A/B and Table2 C chairs. `CafeV2Seated.controller` uses a standalone Humanoid `Sitting_Idle_Loop`; its existing masked Dialogue/Gesture/Carry layers remain. `CafeSeatedFootPose` keeps model-specific feet on the floor through Animator IK. Actor roots have scale 1, root motion is off, and seated Facing visual yaw is zero; HeadGaze and Nod remain available. SeatAnchors are authoring references, not runtime seat ownership.

Merchant and RegularCustomer keep `cafe.merchant` and `cafe.regular_customer`. New IDs are `cafe.companion_customer` and `cafe.quiet_customer`; event receiver routing and player dialogue candidates include all four. Customer action capabilities are empty. Existing Settings discovers the four definitions and edits independent local overrides without schema or backend changes. A preserved legacy RegularCustomer override can still show “顾客” instead of the new default “沈知意”; restoring that NPC's defaults is an explicit user Settings action.

The initial 2026-10-09 P1 validation used an external Unity copy. Subsequent 2026-10-10 core acceptance exercised isolated CafeDemoV2 Play through the actual Editor. These historical runs and the Git delivery rechecks are distinguished in [the public summary](../../../../Docs/RELEASE_STATUS.md); a standalone build is still not verified.

Continuous ambient conversation, P3 emotion expressions and P4 random idle are not implemented. `Sitting_Talking_Loop` is an imported compatibility resource only. This scene is already configured; the one-time `CafeV2NpcP1Setup` authoring menu is not needed again and must not be rerun over updated Persona/scene authoring.

## Memory, continuation and grounding — 2026-10-10

Fixed single-player UID is `cafe-local-player-001`. Four NPCs retain separate canonical Memory owners. Displayed NPC self-statements can form personal memories with an explicit self-report source limitation; they cannot prove Player/shared history or executed world actions. Real successful bring_object can form an executed Episodic and be recalled after a Backend restart.

DialogueContinuationScheduler is wired but disabled by default, waits 4 seconds and permits at most one continuation opportunity per Player input. It reuses the event/cognition pipeline; Player input cancels old continuations, and only successful displayed/ACKed responses become Experience. The opportunity can choose NO_ACTION.

Current observed seats and visible occupant identities use the observer's own perspective. Unknown is not empty; Persona familiarity is not proof of today's arrival/current position. Missing beside geometry remains an explicit limitation. Historical real acceptance and current delivery checks are separated in [the public validation summary](../../../../Docs/RELEASE_STATUS.md).
