using System;
using System.Collections;
using System.IO;
using System.Linq;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Events;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Settings;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    public abstract class PlayerPresentationFixture
    {
        protected CafeDemoPlayerController player;
        protected PlayerEmoteController emote;
        protected PlayerLocomotionPresenter locomotion;
        protected Animator animator;
        protected WorldEntity merchant, customer;
        protected WorldStateService world;
        protected Keyboard keyboard;
        private InputSettings.BackgroundBehavior backgroundBehavior;
        private bool runInBackground;
        protected virtual bool UsesStartupPanel => true;
        protected virtual bool DisableDailyDemo => true;
        protected const string Evidence = "C:/Users/95265/AppData/Local/Temp/embodiednpc-player-p1";
        [UnitySetUp] public IEnumerator SetUp()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
            // Scene demo timers are opt-in within deterministic fixtures; no asset changes are saved.
            if (DisableDailyDemo)
                foreach (var trigger in Object.FindObjectsByType<EmbodiedNPC.Routines.DailyBehaviorDemoTrigger>(FindObjectsSortMode.None))
                    trigger.ConfigureDemo(false, 15, 35);
            var backend = Object.FindAnyObjectByType<BackendWebSocketClient>();
            var so = new SerializedObject(backend); so.FindProperty("connectOnStart").boolValue = false;
            if (!UsesStartupPanel) so.FindProperty("serverUrl").stringValue = "ws://127.0.0.1:0/ws";
            so.ApplyModifiedPropertiesWithoutUndo();
            if (!UsesStartupPanel) backend.enabled = false; // Selecting a dialogue target may explicitly reconnect.
            Object.FindAnyObjectByType<CafeDemoDeveloperPanel>().enabled = false;
            Object.FindAnyObjectByType<FirstRunModelSetup>(FindObjectsInactive.Include).enabled = false;
            if (!UsesStartupPanel)
            {
                foreach (var systemSource in Object.FindObjectsByType<EmbodiedNPC.Routines.ServiceZoneDwellCognitionSource>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    systemSource.enabled = false; // Autonomous tests explicitly opt in after installing their Fake transport.
                foreach (var source in Object.FindObjectsByType<EmbodiedNPC.Routines.CounterCleaningCognitionSource>(FindObjectsInactive.Include, FindObjectsSortMode.None)) source.enabled = false;
                foreach (var trace in Object.FindObjectsByType<RuntimeTraceRecorder>(FindObjectsInactive.Include, FindObjectsSortMode.None)) trace.enabled = false;
                foreach (var network in Object.FindObjectsByType<BackendWebSocketClient>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    var fields = new SerializedObject(network);
                    fields.FindProperty("connectOnStart").boolValue = false;
                    fields.FindProperty("serverUrl").stringValue = "ws://127.0.0.1:0/ws";
                    fields.ApplyModifiedPropertiesWithoutUndo(); network.enabled = false;
                    Assert.That(network.enabled, Is.False, "Offline fixture must disable every network component before Play Mode.");
                }
                foreach (var config in Object.FindObjectsByType<ModelConfigClient>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    config.enabled = false;
                foreach (var form in Object.FindObjectsByType<FirstRunModelSetup>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    form.enabled = false;
                foreach (var developer in Object.FindObjectsByType<CafeDemoDeveloperPanel>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    developer.enabled = false;
                foreach (var history in Object.FindObjectsByType<EmbodiedNPC.Dialogue.Presentation.DialogueHistoryPanel>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    history.enabled = false;
                Assert.That(Object.FindAnyObjectByType<FirstRunModelSetup>(FindObjectsInactive.Include).enabled, Is.False);
                Assert.That(Object.FindAnyObjectByType<CafeDemoDeveloperPanel>().enabled, Is.False);
            }
            yield return new EnterPlayMode();
            runInBackground = Application.runInBackground;
            Application.runInBackground = true;
            backgroundBehavior = InputSystem.settings.backgroundBehavior;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            keyboard = InputSystem.AddDevice<Keyboard>("P1TestKeyboard"); InputSystem.EnableDevice(keyboard);
            player = Object.FindAnyObjectByType<CafeDemoPlayerController>();
            emote = player.GetComponent<PlayerEmoteController>(); locomotion = player.GetComponent<PlayerLocomotionPresenter>();
            animator = player.GetComponentInChildren<Animator>(); world = Object.FindAnyObjectByType<WorldStateService>();
            var entities = Object.FindObjectsByType<WorldEntity>();
            merchant = entities.Single(e => e.EntityId == "npc_cafe_01");
            customer = entities.Single(e => e.EntityId == "npc_cafe_regular_01");
            var panel = Object.FindAnyObjectByType<CafeDemoDeveloperPanel>();
            if (UsesStartupPanel) { panel.ApplyStartupConfig(new ModelConfigView { configured = true }); panel.Close(); }
            else Object.FindAnyObjectByType<DialogueController>().SetSetupReady(true);
        }
        [UnityTearDown] public IEnumerator TearDown()
        {
            if (keyboard != null) InputSystem.RemoveDevice(keyboard);
            InputSystem.settings.backgroundBehavior = backgroundBehavior;
            Application.runInBackground = runInBackground;
            if (Application.isPlaying) yield return new ExitPlayMode();
        }
        protected static IEnumerator Wait(float seconds)
        {
            float until = Time.time + seconds, deadline = Time.realtimeSinceStartup + 30f;
            while (Time.time < until && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(Time.time, Is.GreaterThanOrEqualTo(until), "Play Mode must advance simulation time.");
        }
        protected void Keys(params Key[] keys)
        {
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(keys)); InputSystem.Update();
            // Synthetic input can arrive after Update in the EditMode runner. Use the real input adapter.
            emote.SendMessage("Update");
        }
        protected void Place(Vector3 position)
        {
            player.Controller.enabled = false; player.transform.position = position;
            player.Controller.enabled = true; Physics.SyncTransforms();
        }
        protected void ControlledPerception()
        {
            foreach (var entity in new[] { merchant, customer })
            {
                entity.GetComponent<VisionSensor>().enabled = false;
                var proximity = entity.GetComponent<EmbodiedNPC.Perception.ProximitySensor>();
                if (proximity != null) proximity.enabled = false;
                var visualBridge = entity.GetComponent<VisualObservationAgentEventBridge>();
                if (visualBridge != null) visualBridge.enabled = false;
                entity.GetComponent<AgentOrchestrator>().CancelCurrentRun();
            }
        }
        protected void Capture(string name)
        {
            if (!File.Exists(Evidence + "/capture.flag")) return;
            var camera = Camera.main; var previous = camera.targetTexture; var active = RenderTexture.active;
            var rt = new RenderTexture(1280, 720, 24); var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            try
            {
                camera.targetTexture = rt; camera.Render(); RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); tex.Apply();
                File.WriteAllBytes(Evidence + "/" + name + ".png", tex.EncodeToPNG());
            }
            finally { camera.targetTexture = previous; RenderTexture.active = active; Object.Destroy(rt); Object.Destroy(tex); }
        }
    }

    public sealed class PlayerPresentationTests : PlayerPresentationFixture
    {
        [Test] public void Assets_MinimalControllerExistingClipsMaskAndGOnly()
        {
            var c = (AnimatorController)animator.runtimeAnimatorController;
            Assert.That(c.layers.Select(l => l.name), Is.EqualTo(new[] { "Base Layer", "EmoteUpperBody" }));
            Assert.That(c.parameters.Select(p => p.name), Is.EqualTo(new[] { "Speed" }));
            var tree = (BlendTree)c.layers[0].stateMachine.defaultState.motion;
            Assert.That(tree.children.Select(x => AssetDatabase.GetAssetPath(x.motion)), Is.EqualTo(new[] {
                "Assets/CafeDemo/Animations/ThirdParty/Quaternius/Idle_Loop.anim", "Assets/CafeDemo/Animations/ThirdParty/Quaternius/Walk_Loop.anim" }));
            Assert.That(tree.children[1].timeScale, Is.EqualTo(3.55f));
            var upper = c.layers[1];
            Assert.That(upper.stateMachine.states.Select(s => s.state.name), Is.EquivalentTo(new[] { "Empty", "Wave" }));
            Assert.That(upper.defaultWeight, Is.Zero);
            Assert.That(upper.avatarMask, Is.SameAs(AssetDatabase.LoadAssetAtPath<AvatarMask>("Assets/CafeDemo/Animations/CafeDialogueUpperBody.mask")));
            foreach (var part in new[] { AvatarMaskBodyPart.Root, AvatarMaskBodyPart.Head, AvatarMaskBodyPart.LeftLeg, AvatarMaskBodyPart.RightLeg })
                Assert.That(upper.avatarMask.GetHumanoidBodyPartActive(part), Is.False);
            Assert.That(upper.stateMachine.states.Single(s => s.state.name == "Wave").state.motion,
                Is.SameAs(AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/CafeDemo/Animations/ThirdParty/KayKit/Waving.anim")));
            Assert.That(animator.applyRootMotion, Is.False); Assert.That(animator.isHuman, Is.True);
            Assert.That(new SerializedObject(player).FindProperty("moveSpeed").floatValue, Is.EqualTo(2.6f));
            var actions = AssetDatabase.LoadAssetAtPath<InputActionAsset>("Assets/InputSystem_Actions.inputactions");
            Assert.That(actions.FindAction("Player/Wave").bindings.Select(b => b.path), Is.EqualTo(new[] { "<Keyboard>/g" }));
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/CafeDemo/Prefabs/Characters/PlayerVisual.prefab");
            Assert.That(prefab.GetComponentInChildren<Animator>().runtimeAnimatorController, Is.SameAs(c));
        }

        [UnityTest] public IEnumerator ActualMovement_IdleWalkStopAndTurn_NoEmote()
        {
            ControlledPerception(); int events = 0, runs = 0; emote.EmotePerformed += _ => events++;
            merchant.GetComponent<AgentOrchestrator>().RunStarted += _ => runs++;
            customer.GetComponent<AgentOrchestrator>().RunStarted += _ => runs++;
            Keys(); yield return Wait(.2f);
            Assert.That(locomotion.NormalizedSpeed, Is.Zero); Assert.That(animator.GetFloat("Speed"), Is.LessThan(.01f)); Capture("idle");
            Vector3 start = player.transform.position; Quaternion foot = animator.GetBoneTransform(HumanBodyBones.RightFoot).localRotation;
            Keys(Key.W); yield return Wait(.75f);
            Assert.That(Vector3.Distance(start, player.transform.position), Is.GreaterThan(.5f));
            Assert.That(locomotion.NormalizedSpeed, Is.GreaterThan(.9f)); Assert.That(animator.GetFloat("Speed"), Is.GreaterThan(.9f));
            Assert.That(Quaternion.Angle(foot, animator.GetBoneTransform(HumanBodyBones.RightFoot).localRotation), Is.GreaterThan(5)); Capture("walk");
            Quaternion forward = player.transform.rotation; Keys(Key.D); yield return Wait(.3f);
            Assert.That(Quaternion.Angle(forward, player.transform.rotation), Is.GreaterThan(30)); Capture("turn");
            Keys(); yield return Wait(.35f); Capture("stopped");
            Assert.That(locomotion.NormalizedSpeed, Is.LessThan(.01f)); Assert.That(animator.GetFloat("Speed"), Is.LessThan(.05f));
            Assert.That(events, Is.Zero); Assert.That(runs, Is.Zero); Assert.That(animator.applyRootMotion, Is.False);
        }

        [UnityTest] public IEnumerator WallCollision_InputDoesNotProduceFullWalk()
        {
            ControlledPerception(); var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Vector3 forward = Vector3.ProjectOnPlane(player.View.forward, Vector3.up).normalized;
            wall.transform.position = player.transform.position + forward * .9f + Vector3.up;
            wall.transform.rotation = Quaternion.LookRotation(forward); wall.transform.localScale = new Vector3(4, 2, .3f);
            Physics.SyncTransforms(); Keys(Key.W); yield return Wait(.65f);
            Assert.That(Vector3.ProjectOnPlane(player.Controller.velocity, Vector3.up).magnitude, Is.LessThan(.1f));
            Assert.That(locomotion.NormalizedSpeed, Is.LessThan(.05f)); Assert.That(animator.GetFloat("Speed"), Is.LessThan(.1f));
            Keys(); Object.Destroy(wall);
        }

        [UnityTest] public IEnumerator G_WavePublishesAfterStateEntry_Once_NoStack_ThenEmpty()
        {
            ControlledPerception(); int count = 0; bool visualAtPublish = false; PlayerEmotePerformed value = null;
            emote.EmotePerformed += e => { count++; value = e; visualAtPublish = animator.GetCurrentAnimatorStateInfo(1).IsName("EmoteUpperBody.Wave"); };
            Keys(Key.G); Assert.That(count, Is.Zero, "Input alone must not publish before Animator state entry.");
            Assert.That(emote.IsActive, Is.True); yield return Wait(.4f); Capture("idle-wave");
            Assert.That(count, Is.EqualTo(1)); Assert.That(visualAtPublish, Is.True);
            Assert.That(value.ActorEntityId, Is.EqualTo("player_01")); Assert.That(value.EmoteId, Is.EqualTo("wave"));
            Assert.That(value.EventId, Is.Not.Empty); Assert.That(value.OccurredAt, Is.GreaterThan(0));
            float phase = animator.GetCurrentAnimatorStateInfo(1).normalizedTime;
            Keys(); Keys(Key.G); yield return Wait(.3f);
            Assert.That(count, Is.EqualTo(1)); Assert.That(animator.GetCurrentAnimatorStateInfo(1).normalizedTime, Is.GreaterThan(phase));
            yield return Wait(2.2f); Keys(); Capture("wave-complete");
            Assert.That(emote.IsActive, Is.False); Assert.That(animator.GetCurrentAnimatorStateInfo(1).IsName("EmoteUpperBody.Empty"), Is.True);
            Assert.That(animator.GetLayerWeight(1), Is.Zero); Assert.That(count, Is.EqualTo(1));
            Keys(Key.G); yield return Wait(.3f); Assert.That(count, Is.EqualTo(2)); Keys();
        }

        [UnityTest] public IEnumerator WalkPlusWave_KeepsMovementLegsAndWorldTruth()
        {
            ControlledPerception(); int revision = world.WorldRevision; int count = 0; emote.EmotePerformed += _ => count++;
            var held = Object.FindObjectsByType<EmbodiedNPC.Interaction.HeldItemState>().Select(h => (h.IsHeld, h.HolderEntityId)).ToArray();
            Keys(Key.W); yield return Wait(.3f); var start = player.transform.position;
            Keys(Key.W, Key.G); yield return Wait(.5f); Capture("walk-wave");
            Assert.That(Vector3.Distance(start, player.transform.position), Is.GreaterThan(.6f));
            Assert.That(animator.GetCurrentAnimatorStateInfo(0).IsName("Locomotion"), Is.True);
            Assert.That(animator.GetFloat("Speed"), Is.GreaterThan(.9f));
            Assert.That(animator.GetCurrentAnimatorStateInfo(1).IsName("EmoteUpperBody.Wave"), Is.True);
            Quaternion foot = animator.GetBoneTransform(HumanBodyBones.RightFoot).localRotation;
            yield return Wait(.12f);
            Assert.That(Quaternion.Angle(foot, animator.GetBoneTransform(HumanBodyBones.RightFoot).localRotation), Is.GreaterThan(5));
            Keys(); yield return Wait(2f);
            Assert.That(world.WorldRevision, Is.EqualTo(revision));
            Assert.That(Object.FindObjectsByType<EmbodiedNPC.Interaction.HeldItemState>().Select(h => (h.IsHeld, h.HolderEntityId)).ToArray(), Is.EqualTo(held));
            Assert.That(count, Is.EqualTo(1)); Assert.That(animator.applyRootMotion, Is.False);
        }

        [UnityTest] public IEnumerator DialogueAndSettings_SuspendG_NoVisualOrEvent()
        {
            int events = 0; emote.EmotePerformed += _ => events++;
            var panel = Object.FindAnyObjectByType<CafeDemoDeveloperPanel>(); panel.Open();
            Assert.That(player.IsInputSuspended, Is.True); Keys(Key.G); yield return Wait(.3f);
            Assert.That(emote.IsActive, Is.False); Assert.That(events, Is.Zero); Keys(); panel.Close();
            Place(merchant.transform.position + merchant.transform.forward * 1.5f);
            var dialogue = Object.FindAnyObjectByType<DialogueController>();
            Assert.That(dialogue.SelectTarget(merchant.GetComponent<DialogueParticipant>()), Is.True);
            Assert.That(player.IsInputSuspended, Is.True); Keys(Key.G); yield return Wait(.3f);
            Assert.That(emote.IsActive, Is.False); Assert.That(events, Is.Zero); Keys(); dialogue.HideDialoguePresentation();
            Assert.That(player.IsInputSuspended, Is.False);
        }

        [UnityTest] public IEnumerator DisableReenable_CancelsWithoutReplayOrDuplicateInput()
        {
            int count = 0; emote.EmotePerformed += _ => count++;
            Keys(Key.G); yield return Wait(.3f); Assert.That(count, Is.EqualTo(1));
            Keys(); emote.enabled = false; Assert.That(emote.IsActive, Is.False); Assert.That(animator.GetLayerWeight(1), Is.Zero);
            emote.enabled = true; yield return Wait(.2f); Assert.That(count, Is.EqualTo(1));
            Keys(Key.G); yield return Wait(.3f); Assert.That(count, Is.EqualTo(2)); Keys();
        }
    }
}
