# Cafe local ambient clips

These are baked Humanoid muscle curves authored inside this project with
`CafeAmbientSetup`. No external asset was downloaded for this feature and no
runtime IK, Animation Rigging package, root motion or procedural bone writer is
required.

`CafeSeatedDrink.anim` is a 6.2 second right-arm/finger action calibrated on the
existing Ao VRM (Wen). It uses the existing CC0 Quaternius seated idle as its
authoring reference. Reach/contact is at 1.1 s, drinking occupies roughly
2.4–4.1 s, release is at 5.1 s, and recovery ends at 6.2 s. The resulting clip
contains only right shoulder/arm/forearm/wrist/finger curves. Body, head, root
and legs are excluded by the existing conversation arms mask. Base SeatedIdle
and the existing foot IK remain active.

Wen's wrist approaches behind/outside the cup, with the elbow kept close to the
body and fingers directed around the cup side. The cup position and rotation are
calibrated in the actual evaluated hand's local space and follow that same rigid
grip throughout the lift; the wrist animation supplies the small drinking tilt.
The drinking target accounts for the cup rim height. Release withdraws
the open hand sideways. `CafeAmbientSetup.RebuildDrink` rebakes only this motion
and its scene grip calibration, leaving counter work setup alone.

Conversation requests a bounded return under the same arms lease: a held cup
blends into the existing lowering tail, while an uncommitted reach fades out.
Dialogue arms wait for that lease to finish. Formal tasks, emergency, disable
and unload still revoke immediately and restore the borrowed prop.

The supplied PickUp_Table, PickUp, Use_Item and Holding_B were actually previewed
on Ao over the seated base. They do not provide a complete seated drink/return
motion. Holding_B is not used as a drinking hand pose. Finger curves have their
Animator binding names (`RightHand.Index.1 Stretched`, etc.), which differ from
HumanTrait names (`Right Index 1 Stretched`).

`CafeAmbientWipe.anim` retains the existing project-authored CafeCleanCounter
arm presentation as its starting reference and fits the real finger contact
patch on Vivi. The existing cloth follows that contact patch while the lease
is valid, then restores its exact formal local binding. Its standing reference is the existing
Idle_Quiet_Vivi. It plays in the existing cleaning layer as AmbientClean; the
formal CleanCounter state and original clip are unchanged. It is presentation
only and cannot commit counter cleanliness.

Ambient cleaning blends around the middle contact segment for the duration
selected by DailyBehavior (merchant dwell 7–11 seconds), then plays the existing
exit tail. Formal CleanCounter playback does not use this contact loop.
After arriving at a cleanable ambient work point, DailyBehavior selects wiping
after a short pause instead of randomly walking away again. A task check whose
gates are closed cannot indefinitely reserve the local idle scheduler.

Casual attention requires current visual evidence from the NPC's existing
VisionSensor (its own FOV and occlusion), with a 3 m entry / 3.6 m maintenance
range. Proximity, a second narrow root cone and the player's camera do not
establish NPC sight. The bounded head turn covers that sensor's side-front FOV;
the fixed seat and gameplay root remain unchanged.

Existing references and licenses: `ThirdParty/Quaternius/SOURCE.md`,
`ThirdParty/Quaternius/LICENSE.txt`, `ThirdParty/KayKit/SOURCE.md` and
`ThirdParty/KayKit/LICENSE.txt`. New authored curves are first-party project
content under the repository license.

The scene contains one existing cup-only prefab for Wen, replacing only her
original decorative cup-and-saucer mesh. It has no WorldEntity/HeldItemState,
does not impersonate cup_01, and is never instantiated per action. During a
drink it follows the hand anchor while retaining its original scene parent;
completion/cancellation restores the exact saved local transform.

To extend to another seated NPC, configure the same Presenter/admission/Daily
profile fields, a distinct decorative cup, table and hand anchors, grip offsets,
timings and interval scale. Retargeting is Humanoid; each model/seat still needs
visual contact calibration before enabling it. Jiang uses the same Presenter with the quieter Humanoid override and an independent interval scale. Shen remains disabled.

CafeServeSnack.anim is a first-party, baked arms-only reach/hold/retract presentation produced by CafeSnackServiceSetup from the existing licensed project animation reference. It owns no food prop and does not certify a service outcome.
