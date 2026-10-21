# KayKit Wave and Carry provenance

## Player emote panel: Cheering and Flexing (2026-10-15)

The same supplied CC0 1.1 package provides `Cheering` from
`Animations/fbx/Rig_Medium/Rig_Medium_Simulation.fbx` and `Flexing` from
`Animations/fbx/Rig_Large/Rig_Large_Simulation.fbx`. Unity imports each temporary
source as Humanoid/Create From This Model with Bake Axis Conversion, then clones
only the selected motion to the independent `Cheering.anim` / `Flexing.anim`.
Both are non-looping, have baked root rotation/Y/XZ and no animation events.
The temporary imports are removed; no source FBX, source Avatar or mannequin
mesh is required in production. CafeV2Player reuses EmoteUpperBody and its mask;
player movement remains authoritative and Animator root motion remains disabled.
These two actions are presentation only and add no NPC observation contract.

Package: **KayKit: Character Animations 1.1**, by **Kay Lousberg**.
Local supplied package: `SceneResources/KayKit_Character_Animations_1.1`.
Author: https://www.kaylousberg.com/ ; package-author catalog: https://kaylousberg.itch.io/ .
The supplied `License.txt` explicitly identifies this package/version and **CC0**,
including personal, educational and commercial use. `LICENSE.txt` is its exact copy.
No attribution is required; provenance is retained for review.

## Inventory and selection

The supplied package has 37 files: 14 animation FBX files and their 14 GLB equivalents,
2 mannequin FBX files and 2 GLB equivalents, one texture, one license and three URL links.
Medium has eight animation categories; Large has six. There is no separate InPlace/RM tree
and no README. All 16 GLB animation inventories were inspected. The only Wave-like take
(Wave/Waving/Hello/Greeting/Greet/HandWave) is **Waving**, in
`Animations/fbx/Rig_Medium/Rig_Medium_Simulation.fbx` and its GLB equivalent.
Large has no Wave-like take. Other simulation takes are not alternative Wave clips.

FBX SHA256: `7A75D775E966F28E0D365147C3CD844D3641B40FA97BC591F33B9FC609463142`.
Imported in Unity 6000.5.10f1 as Humanoid, CreateFromThisModel, BakeAxisConversion,
no materials. The valid Human source avatar has 17 bones. Only **Waving** was selected
for extraction, baked root rotation/Y/XZ, non-looping, state speed 1.
The formal imported AnimationClip was cloned with Unity Object.Instantiate/CreateAsset
into `Waving.anim`; the temporary source FBX was removed. The standalone clip has
no source FBX dependency, no events, and lasts **2.13333344 seconds**.

## Source motion and retarget limits

Source has no locomotion (average speed approximately zero); RootT still contains
small body sway: X 0.05010–0.09247, Y 0.96198–0.97592, Z -0.00834–-0.00643.
RootQ/hips and legs have some sway, and the source head has motion. Spine/chest/upper
chest muscles and neck muscles are constant. All source finger muscles are constant:
this is an arm/hand wave, not an independently animated finger articulation.
The existing `CafeDialogueUpperBody.mask` excludes Root, Head and both legs, and includes
body and arms. This protects ActorRoot/navigation and leaves HeadGaze as the head writer.
No new mask, IK or procedural arm animation was added.

Actual Animator retarget sampling on woman_2 and man_2 showed valid Human avatars,
zero ActorRoot drift and a right upper-arm angular range of about 48.18 degrees;
source fingers remain unanimated. CafeDemo MainCamera visual review is required in
addition to these technical checks. Talking, Walk and procedural Nod keep their
existing assets and implementations. No other KayKit clips or full source package
are part of the ER-4 production change.

## ER-5 Carry selection

The actual GLB inventories were searched for Carry/Carrying/Hold/Holding/Object/Item/
Box/Crate/Tray. Medium Tools supplies the three relevant holding candidates:
`Holding_A`, `Holding_B`, `Holding_C`. Each is 1.0 s; the source importer defaults
to non-looping. All three were audited as Humanoid with a valid source avatar,
then evaluated on Cafe Merchant's existing woman_2 avatar in Play Mode. Loop Time /
Loop Pose and baked root rotation/Y/XZ were authored for this sustained holding use.
UAL Standard's 43 takes have no dedicated Carry loop; Idle_Torch and weapon poses
are not a small-cup carry choice. KayKit's weapon-holding runs are not consumed.

- Holding_A: arms extended forward and wide, like supporting a larger object.
- Holding_B: elbows lower, hands closer to the waist; selected for the small cup.
- Holding_C: hands further across the front with a more asymmetric torso stance.

Only **Holding_B.anim** is added to production; the audit controller/source FBX
were removed. Source: `Animations/fbx/Rig_Medium/Rig_Medium_Tools.fbx`.
SHA256: `83FE62F77D03CAA1B221D3CE1E7B9377F486CFF978204061784F9944CAA00A32`.
Same package, license and Humanoid import method as Wave. The cloned standalone
clip is looping, has no animation events and no FBX/avatar dependency.

All three include full-body curves; their source RootT ranges are:

| Take | X | Y | Z |
| --- | --- | --- | --- |
| Holding_A | -0.001357..-0.001288 | 0.963931..0.966909 | -0.001770..0.001273 |
| Holding_B | -0.003258..-0.003201 | 0.950930..0.955006 | -0.032516..-0.030019 |
| Holding_C | -0.043951 constant | 0.948957..0.953093 | -0.029547..-0.026600 |

These small pose/sway curves are not locomotion. `CarryUpperBody` uses the existing
CafeDialogueUpperBody mask: Body/Arms/Fingers on, Root/Head/Legs off. Base Walk
remains unchanged. The low-poly model's hands use the existing retargeted shape;
this change adds no finger articulation or IK. The Merchant-specific hold-point
offset positions the cup at the right palm, while the existing GrabPoint alignment
and WorldState ownership transactions continue to attach/transfer the item.

## VRM NPC idle and expressive-layer compatibility (2026-10-22)

`Idle_A.anim` is extracted from KayKit Character Animations 1.1,
`Animations/fbx/Rig_Medium/Rig_Medium_General.fbx`, by Kay Lousberg,
under the same supplied CC0 license (`LICENSE.txt`). Imported as Humanoid /
Create From This Model with Bake Axis Conversion and no materials. The 1.0667 s
standalone clip loops with Loop Pose, baked root rotation/Y/XZ, and root
orientation based on Body Orientation (Keep Original Orientation disabled).
It has no events or source FBX dependency.

CafeExpressive and CafeV2Expressive use this relaxed idle; CafeV2Seated retains
its seated base motion. The Dialogue layer reuses Idle_A as a conservative
arm-only fallback instead of the broad retargeted Talking motion. Facial
expressions, head/nod presentation and the existing KayKit Waving remain.
Dialogue and GestureUpperBody use CafeConversationArms (arms/fingers only),
preventing these layers from replacing the hips, torso, legs or head. Carry,
drink-preparation and cleaning masks remain separate and unchanged.

CafeV2Expressive Locomotion Foot IK is disabled: on Vivi's VRM 1 Avatar the
solver widened the idle stance and distorted the existing walk. The Walk clips,
blend thresholds, playback rates, locomotion speeds and movement code are unchanged.
