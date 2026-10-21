# Quaternius standalone Humanoid animations

- Asset: Universal Animation Library
- Author: Quaternius
- Edition: Standard
- Original clip: `Armature|Walk_Loop` (`Walk_Loop`)
- License: CC0 1.0 Universal; original package notice is preserved in LICENSE.txt.
- Official source: https://quaternius.com/packs/universalanimationlibrary.html
- Official distribution: https://quaternius.itch.io/universal-animation-library
- Original local source: `SceneResources/Universal Animation Library[Standard]/Unity/UAL1_Standard.fbx`
- Source FBX SHA-256: `21B32D912DA3CB93426D974FB945E86F5B2E86970ACD2CE89905E0FBF9F1DCC2`

The consumed `Walk_Loop.anim`, `Idle_Talking_Loop.anim`, `Sitting_Idle_Loop.anim` and `Sitting_Talking_Loop.anim` are redistributed. The source library and temporary import
FBX are not production assets. Unity 6000.5.10f1 imported the non-root-motion
FBX with Bake Axis Conversion, Humanoid / Create From This Model (valid Avatar,
52 mapped human bones), and root motion node `root`. Walk frames 0–40 at 30 fps
use Loop Time / Loop Pose and bake root rotation, Y and XZ into pose.

The imported Humanoid sub-asset was cloned with Object.Instantiate and saved
with AssetDatabase.CreateAsset. Its independent serialized muscle data has no
dependency on the FBX or source Avatar. After removing the temporary FBX, Unity
reimport and Cafe retarget validation use only the clip and the character's
existing Humanoid Avatar. This preserves the single-clip consumer boundary
instead of shipping the approximately 23.8 MB, 43-clip source FBX.

Before the 2026-10-21 candidate remediation, CafeExpressive.controller reused a separately licensed low-poly Idle clip and blended
Idle (Speed 0) / Walk (Speed 1). Speed comes from the existing LocomotionPresenter;
Walk child playback is 3.0x for the existing 2.2 m/s NavMeshAgent. The initial
1.6x trial slid noticeably; the 3.0x Cafe player-camera comparison was accepted
with minor sliding during turns/acceleration. The gameplay speed is unchanged.
Animator root motion remains disabled. No animation events or movement authority
are added.

## ER-4 source audit (2026-10-02)

The same non-RM FBX was formally imported through Unity AssetDatabase as Humanoid,
Create From This Model, Bake Axis Conversion, root node `root`, without materials.
All 43 non-preview takes were inspected. Semantic Talk/Talking/Conversation/
Speaking/Wave/Waving/Greeting/Nod/Agree/Yes search found exactly two candidates:

| Take | Seconds | Source loop default / configured | Human | Decision |
| --- | --- | --- | --- | --- |
| Armature\|Idle_Talking_Loop | 2.933333 | false / true with Loop Pose | yes | Selected standing conversation |
| Armature\|Sitting_Talking_Loop | 2.933333 | false / true with Loop Pose | yes | Rejected seated posture for standing Cafe actors |

Both contain body, arm and Head/Neck muscle curves. Selected standing take has
left/right arm Down-Up muscle ranges 0.4953/0.1806, UpperChest Front-Back 0.1600,
Head Nod 0.2190 and Neck Nod 0.0800. RootT X/Z residual ranges are approximately
0.00665/0.00794 (Y constant): root baking alone is not an authority guarantee.
The production AvatarMask excludes Root, both Legs and Head; applyRootMotion
remains false. Head/Neck source motion is excluded to preserve Head Gaze.

Editor Animator evaluation sampled 90 steps over each candidate on both woman_2
and man_2, with valid existing Humanoid Avatars and zero Actor Root drift.
Standing upper arm angular ranges were about 18.97/14.69 degrees, Head 5.37,
Neck/Chest 3.24; seated upper arms 19.21/14.99, Head 6.28, Neck/Chest 2.34.
These retarget observations establish playback, not final Cafe visual acceptance.

The consumed take is frames 0-88 at 30fps, cloned with Unity Object.Instantiate
and AssetDatabase.CreateAsset; it retains its actual Humanoid muscle animation.
After temporary FBX deletion and reimport its only asset dependency is itself.
No FBX animation events were added. Existing CC0 LICENSE.txt covers this clip.

No Wave/Waving/Greeting or Nod/Agree/Yes take exists among the 43. Wave stays
unavailable; no substitute, procedural arms or legacy Y Bot asset is used.
Nod = project-owned procedural presentation, not a Quaternius asset.

Complete take inventory (not redistributed): A_TPose, Crouch_Fwd_Loop,
Crouch_Idle_Loop, Dance_Loop, Death01, Driving_Loop, Fixing_Kneeling, Hit_Chest,
Hit_Head, Idle_Loop, Idle_Talking_Loop, Idle_Torch_Loop, Interact, Jog_Fwd_Loop,
Jump_Land, Jump_Loop, Jump_Start, PickUp_Table, Pistol_Aim_Down,
Pistol_Aim_Neutral, Pistol_Aim_Up, Pistol_Idle_Loop, Pistol_Reload, Pistol_Shoot,
Punch_Cross, Punch_Jab, Push_Loop, Roll, Sitting_Enter, Sitting_Exit,
Sitting_Idle_Loop, Sitting_Talking_Loop, Spell_Simple_Enter, Spell_Simple_Exit,
Spell_Simple_Idle_Loop, Spell_Simple_Shoot, Sprint_Loop, Swim_Fwd_Loop,
Swim_Idle_Loop, Sword_Attack, Sword_Idle, Walk_Formal_Loop, Walk_Loop.

## CafeDemoV2 four NPC P1 (2026-10-09)

The existing local non-root-motion FBX supplied two additional standalone Humanoid clips:

| Clip | Length | Use |
| --- | --- | --- |
| Sitting_Idle_Loop | 1.666667 s | CafeV2Seated base layer |
| Sitting_Talking_Loop | 2.933333 s | Compatibility resource only; no ambient conversation |

Both use Loop Time / Loop Pose and bake root rotation, Y and XZ into pose. Unity 6000.5.10f1 imported the same original FBX with a valid Humanoid Avatar and cloned the clips through Object.Instantiate / AssetDatabase.CreateAsset. AssetDatabase.GetDependencies returns only each standalone clip itself; the temporary FBX and its Avatar are not shipped. Both clips were sampled on all four NPC Humanoid Avatars in the external validation project. Actual seated Play Mode uses Sitting_Idle_Loop plus visual foot IK, with actor root motion disabled. The existing CC0 notice applies. No new download or dependency was introduced.

## Open-source candidate Idle replacement (2026-10-21)

`Idle_Loop.anim` comes from **Armature|Idle_Loop**, in the same existing Standard
FBX identified above (SHA-256 unchanged). Quaternius is the author; the package's
CC0 1.0 notice in LICENSE.txt applies. The official source page explicitly lists
CC0: https://quaternius.com/packs/universalanimationlibrary.html . No new download
or external asset dependency was introduced. CC0 permits source redistribution,
forking, modification and commercial use; attribution here is voluntary.

Unity 6000.5.10f1 imported this selected 0-75 frame take as Humanoid with Bake Axis
Conversion, Create From This Model, Loop Time/Loop Pose, and baked root rotation,
Y and XZ. Only the resulting independent 2.50000024 s clip is retained, with
130 curve bindings, no animation events and no FBX/source Avatar dependency.
It was cloned from the CC0 import, not from the excluded Asset Store Idle.

The candidate changes only Idle motion references in CafeExpressive, CafePlayer,
CafeV2Expressive and CafeV2Player. Existing Walk timing, seated Idle, Talking,
Wave, Carry masks, parameters and transitions retain their baseline values.
Animator root motion remains disabled. No new runtime animation logic, navigation
authority or gameplay event is introduced.

Retarget sampling covered all five current VRM Humanoid Avatars: valid, zero
ActorRoot drift, continuous loop endpoint. Candidate Play Mode tests also cover
actual carry/walk/turn/give, NPC talking/wave transitions, player idle/walk/stop,
Chinese font coverage and full-body Idle samples. These are focused checks;
they do not constitute full Backend/model-service or Standalone acceptance.
