using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Events;
using EmbodiedNPC.Interaction;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Routines;
using EmbodiedNPC.Settings;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    /// <summary>Opt-in repeatable V2 experiments. No asset saves, model calls or world shortcuts.</summary>
    public sealed class CafeV2LocomotionPolishTests
    {
        private const string Evidence = "D:/EmbodiedNPC/SceneResources/LocomotionPolish/2026-10-08";
        private CafeDemoPlayerController player;
        private WorldEntity merchant, playerEntity, cup;
        private Animator playerAnimator, npcAnimator;
        private NavMeshAgent agent;
        private Keyboard keyboard;
        private Camera camera;
        private readonly List<string> copies = new List<string>();
        private readonly List<string> rows = new List<string>();
        private string directory;
        private int sample, fps;
        private bool capture;
        private float started, oldCapture, oldFixed;
        private int oldTarget, oldVsync;
        private bool oldBackground;
        private InputSettings.BackgroundBehavior oldInputBackground;
        private string phase;
        private Vector3 home;
        private bool configured;
        private float lastRecordedTime, nextCaptureTime;

        [UnitySetUp] public IEnumerator Setup()
        {
            switch (TestContext.CurrentContext.Test.Name)
            {
                case "Smooth": return Begin("Smooth",6,10,300,.08f,4,300,.06f,3.55f,3f);
                case "Responsive": return Begin("Responsive",18,26,540,.04f,10,540,.04f,3.55f,3f);
                case "Balanced": return Begin("Balanced",10,18,420,.06f,6,420,.06f,3.55f,3f);
                case "FineBaseline": return Begin("FineBaseline",0,0,540,.12f,8,540,.1f,3.55f,3f);
                case "FineMid": return Begin("FineMid",10,18,420,.06f,6,420,.06f,3.4f,2.7f);
                case "FineLow": return Begin("FineLow",10,18,420,.06f,6,420,.06f,3.25f,2.5f);
                case "FineResponse": return Begin("FineResponse",12,20,420,.05f,6,420,.05f,3.4f,2.7f);
                case "VisualBaseline": return Begin("VisualBaseline",0,0,540,.12f,8,540,.1f,3.55f,3f);
                case "Final30": return Begin("Final30",10,18,420,.06f,6,420,.06f,3.25f,2.5f,30);
                case "Final60": return Begin("Final60",10,18,420,.06f,6,420,.06f,3.25f,2.5f,60);
                case "Final120": return Begin("Final120",10,18,420,.06f,6,420,.06f,3.25f,2.5f,120);
                case "FinalFocus": return Begin("FinalFocus",10,18,420,.06f,6,420,.06f,3.25f,2.5f);
                case "VisualBaselineFocus": return Begin("VisualBaselineFocus",0,0,540,.12f,8,540,.1f,3.55f,3f);
                default: return Begin("Baseline",0,0,540,.12f,8,540,.1f,3.55f,3f);
            }
        }

        [UnityTearDown] public IEnumerator Cleanup()
        {
            if (!string.IsNullOrEmpty(directory) && rows.Count > 1) Flush();
            if (configured)
            {
                if (keyboard != null) InputSystem.RemoveDevice(keyboard);
                InputSystem.settings.backgroundBehavior = oldInputBackground;
                Time.captureDeltaTime = oldCapture; Time.fixedDeltaTime = oldFixed;
                Application.targetFrameRate = oldTarget; QualitySettings.vSyncCount = oldVsync;
                Application.runInBackground = oldBackground;
                configured = false;
            }
            foreach (var path in copies) AssetDatabase.DeleteAsset(path);
            copies.Clear();
            if (Application.isPlaying) yield return new ExitPlayMode();
        }

        private IEnumerator Begin(string label, float accel, float decel, float turn, float damping,
            float npcAccel, float npcTurn, float npcDamping, float playerPlayback, float npcPlayback, int rate = 60, bool images = true)
        {
            EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
            var backend = new SerializedObject(Object.FindAnyObjectByType<BackendWebSocketClient>());
            backend.FindProperty("connectOnStart").boolValue = false; backend.ApplyModifiedPropertiesWithoutUndo();
            Object.FindAnyObjectByType<FirstRunModelSetup>(FindObjectsInactive.Include).enabled = false;
            yield return new EnterPlayMode();
            Assert.That(Application.isPlaying,Is.True,"Measurements require actual Play Mode.");
            Directory.CreateDirectory(Evidence);
            directory = Path.Combine(Evidence, label); Directory.CreateDirectory(directory);
            if(File.Exists(Path.Combine(directory,"motion.csv")))
            { directory += "-rerun-"+Guid.NewGuid().ToString("N").Substring(0,8); Directory.CreateDirectory(directory); }
            capture = images; fps = rate; sample = 0; rows.Clear();
            rows.Add("frame,time,dt,realtime,phase,actor,x,y,z,yaw,speed,anim,lfx,lfy,lfz,rfx,rfy,rfz,carry,holder,revision");
            player = Object.FindAnyObjectByType<CafeDemoPlayerController>();
            bool production=label.StartsWith("Final");
            if(!production)
            {
                Set(player, "acceleration", accel); Set(player, "deceleration", decel); Set(player, "turnSpeed", turn);
                Set(player.GetComponent<PlayerLocomotionPresenter>(), "dampingSeconds", damping);
            }
            var entities = Object.FindObjectsByType<WorldEntity>(FindObjectsSortMode.None);
            merchant = entities.Single(e => e.EntityId == "npc_cafe_01");
            playerEntity = entities.Single(e => e.EntityId == "player_01"); cup = entities.Single(e => e.EntityId == "cup_01");
            agent = merchant.GetComponent<NavMeshAgent>();
            if(!production)
            {
                agent.acceleration = npcAccel; agent.angularSpeed = npcTurn;
                Set(merchant.GetComponentInChildren<LocomotionPresenter>(), "dampingSeconds", npcDamping);
            }
            playerAnimator = player.GetComponentInChildren<Animator>(); npcAnimator = merchant.GetComponentInChildren<Animator>();
            if(!production) { ClonePlayback(playerAnimator, playerPlayback); ClonePlayback(npcAnimator, npcPlayback); }
            else
            {
                Assert.That(new SerializedObject(player).FindProperty("acceleration").floatValue,Is.EqualTo(10));
                Assert.That(new SerializedObject(player).FindProperty("deceleration").floatValue,Is.EqualTo(18));
                Assert.That(new SerializedObject(player).FindProperty("turnSpeed").floatValue,Is.EqualTo(420));
                Assert.That(new SerializedObject(player.GetComponent<PlayerLocomotionPresenter>()).FindProperty("dampingSeconds").floatValue,Is.EqualTo(.06f));
                Assert.That(new SerializedObject(merchant.GetComponentInChildren<LocomotionPresenter>()).FindProperty("dampingSeconds").floatValue,Is.EqualTo(.06f));
                Assert.That(agent.acceleration,Is.EqualTo(6)); Assert.That(agent.angularSpeed,Is.EqualTo(420));
                Assert.That(agent.stoppingDistance,Is.EqualTo(.12f)); Assert.That(agent.speed,Is.EqualTo(2.2f));
                Assert.That(((BlendTree)((AnimatorController)playerAnimator.runtimeAnimatorController).layers[0].stateMachine.defaultState.motion).children[1].timeScale,Is.EqualTo(3.25f));
                Assert.That(((BlendTree)((AnimatorController)npcAnimator.runtimeAnimatorController).layers[0].stateMachine.defaultState.motion).children[1].timeScale,Is.EqualTo(2.5f));
            }
            File.WriteAllText(Path.Combine(directory,"parameters.json"), Newtonsoft.Json.JsonConvert.SerializeObject(new {
                label, accel, decel, turn, damping, npcAccel, npcTurn, npcDamping, playerPlayback, npcPlayback,
                moveSpeed = 2.6f, npcSpeed = agent.speed, stoppingDistance = agent.stoppingDistance,
                algorithm = "velocity-cap-v2",
                simulationHz = rate, captureHz = 15, images, scene = "CafeDemoV2", scoring = "criteria-v1" }, Newtonsoft.Json.Formatting.Indented));
            oldCapture = Time.captureDeltaTime; oldFixed = Time.fixedDeltaTime; oldTarget = Application.targetFrameRate;
            oldVsync = QualitySettings.vSyncCount; oldBackground = Application.runInBackground;
            oldInputBackground = InputSystem.settings.backgroundBehavior; configured = true;
            Time.captureDeltaTime = 1f / rate; Time.fixedDeltaTime = 1f / rate;
            Application.targetFrameRate = rate; QualitySettings.vSyncCount = 0; Application.runInBackground = true;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            // Rebind after domain reload; runtime clones remain attached to the scene.
            player = Object.FindAnyObjectByType<CafeDemoPlayerController>();
            entities = Object.FindObjectsByType<WorldEntity>(FindObjectsSortMode.None);
            merchant = entities.Single(e => e.EntityId == "npc_cafe_01"); playerEntity = entities.Single(e => e.EntityId == "player_01");
            cup = entities.Single(e => e.EntityId == "cup_01"); agent = merchant.GetComponent<NavMeshAgent>();
            playerAnimator = player.GetComponentInChildren<Animator>(); npcAnimator = merchant.GetComponentInChildren<Animator>();
            keyboard = InputSystem.AddDevice<Keyboard>("V2PolishKeyboard"); InputSystem.EnableDevice(keyboard);
            var panel = Object.FindAnyObjectByType<CafeDemoDeveloperPanel>(); panel.ApplyStartupConfig(new ModelConfigView { configured = true }); panel.Close();
            foreach (var e in entities.Where(e => e.GetComponent<AgentOrchestrator>() != null))
            {
                var driver = e.GetComponent<CognitiveRunDriver>(); if (driver != null) driver.enabled = false;
                var bridge = e.GetComponent<VisualObservationAgentEventBridge>(); if (bridge != null) bridge.enabled = false;
                var proximity = e.GetComponent<EmbodiedNPC.Perception.ProximitySensor>(); if (proximity != null) proximity.enabled = false;
                e.GetComponent<AgentOrchestrator>().CancelCurrentRun();
            }
            merchant.GetComponent<NpcDefaultRoutineController>().enabled = false;
            camera = Camera.main;
            camera.GetComponent<CafeDemoOrbitCamera>().SetDialogueInputSuspended(true);
            Assert.That(player.isActiveAndEnabled && playerAnimator.isHuman && npcAnimator.isHuman, Is.True);
            Assert.That(playerAnimator.applyRootMotion || npcAnimator.applyRootMotion, Is.False);
            home = merchant.GetComponent<ReturnToHomeRoutine>().HomePosition;
            started = Time.time; lastRecordedTime = -1; nextCaptureTime = Time.time;
            phase = "settle"; Keys(); yield return SampleFor(.3f);
        }

        private static void Set(Object target, string field, float value)
        { var so = new SerializedObject(target); so.FindProperty(field).floatValue = value; so.ApplyModifiedPropertiesWithoutUndo(); }
        private void ClonePlayback(Animator animator, float speed)
        {
            // CopyAsset preserves Unity's native controller subasset references; Instantiate does not.
            if (!AssetDatabase.IsValidFolder("Assets/_Recovery")) AssetDatabase.CreateFolder("Assets", "_Recovery");
            string path="Assets/_Recovery/V2Polish-"+Guid.NewGuid().ToString("N")+".controller";
            Assert.That(AssetDatabase.CopyAsset(AssetDatabase.GetAssetPath(animator.runtimeAnimatorController),path),Is.True);
            copies.Add(path);
            var controller=AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            var tree=(BlendTree)controller.layers[0].stateMachine.defaultState.motion;
            var children = tree.children; children[1].timeScale = speed; tree.children = children;
            EditorUtility.SetDirty(tree); AssetDatabase.SaveAssetIfDirty(tree);
            animator.runtimeAnimatorController = controller;
        }
        private void Keys(params Key[] keys)
        { InputSystem.QueueStateEvent(keyboard, new KeyboardState(keys)); InputSystem.Update(); }
        private void Place(Vector3 position)
        {
            Keys(); player.SetDialogueInputSuspended(true); player.Controller.enabled = false;
            player.transform.SetPositionAndRotation(position, Quaternion.identity); player.Controller.enabled = true;
            player.SetDialogueInputSuspended(false); camera.transform.rotation = Quaternion.identity; Physics.SyncTransforms();
        }
        private IEnumerator SampleFor(float seconds)
        {
            float until=Time.time+seconds, deadline=Time.realtimeSinceStartup+60;
            while(Time.time<until && Time.realtimeSinceStartup<deadline) { yield return null; Record(); }
            Assert.That(Time.time,Is.GreaterThanOrEqualTo(until),"Simulation must advance.");
        }
        private void Record()
        {
            if (Time.time <= lastRecordedTime) return;
            lastRecordedTime = Time.time;
            sample++;
            var world = Object.FindAnyObjectByType<WorldStateService>();
            foreach (var a in new[] { playerAnimator, npcAnimator })
            {
                var root = a == playerAnimator ? player.transform : merchant.transform;
                var left = a.GetBoneTransform(HumanBodyBones.LeftFoot).position;
                var right = a.GetBoneTransform(HumanBodyBones.RightFoot).position;
                var v = a == playerAnimator ? player.Controller.velocity : agent.velocity;
                var p = root.position;
                rows.Add(string.Join(",", new object[] { sample, Time.time-started, Time.deltaTime, Time.realtimeSinceStartup,
                    phase, a == playerAnimator ? "player" : "npc", p.x,p.y,p.z,root.eulerAngles.y,
                    Vector3.ProjectOnPlane(v,Vector3.up).magnitude,a.GetFloat("Speed"),left.x,left.y,left.z,right.x,right.y,right.z,
                    merchant.GetComponent<CarryPresenter>().CurrentWeight,cup.GetComponent<HeldItemState>().HolderEntityId,world.WorldRevision }
                    .Select(x=>Convert.ToString(x,CultureInfo.InvariantCulture))));
            }
            if (capture && Time.time >= nextCaptureTime)
            {
                nextCaptureTime = Time.time + 1f / 15;
                var target = phase.StartsWith("npc") || phase.StartsWith("chain") ? merchant.transform : player.transform;
                bool npc = target == merchant.transform;
                var side=directory.Contains("Final")||directory.Contains("VisualBaseline")
                    ? new Vector3(npc ? -1.3f : 1.6f,1.35f,-.6f) : new Vector3(npc ? -1.5f : 1.5f,1.2f,0);
                Capture("side", target.position + side, target.position + Vector3.up * .9f);
                Capture("front", target.position + new Vector3(1f,1.25f,npc ? -1.5f : 1.5f), target.position + Vector3.up * .8f);
                Capture("third", camera.transform.position, camera.transform.position + camera.transform.forward);
            }
        }
        private void Capture(string view, Vector3 position, Vector3 target)
        {
            var rotation = camera.transform.rotation; var priorPosition = camera.transform.position; float fov=camera.fieldOfView;
            var priorTarget = camera.targetTexture; var priorActive = RenderTexture.active;
            var rt = RenderTexture.GetTemporary(640,360,24); var texture = new Texture2D(640,360,TextureFormat.RGB24,false);
            try
            {
                camera.transform.position = position; camera.transform.LookAt(target); camera.targetTexture = rt;
                if((directory.Contains("Fine")||directory.Contains("Final")||directory.Contains("VisualBaseline")) && view!="third") camera.fieldOfView=70f;
                camera.Render(); RenderTexture.active = rt; texture.ReadPixels(new Rect(0,0,640,360),0,0); texture.Apply();
                File.WriteAllBytes(Path.Combine(directory,view+"-"+sample.ToString("D5")+".png"),texture.EncodeToPNG());
            }
            finally { camera.targetTexture = priorTarget; RenderTexture.active = priorActive;
                camera.fieldOfView=fov; camera.transform.SetPositionAndRotation(priorPosition,rotation); RenderTexture.ReleaseTemporary(rt); Object.Destroy(texture); }
        }
        private void Flush() => File.WriteAllLines(Path.Combine(directory,"motion.csv"),rows);

        private IEnumerator PlayerSequence()
        {
            // Measured V2 floor bounds: z >= -1.6; use the clear entrance-side strip.
            Place(new Vector3(-2.5f,.03f,.6f));
            phase="player-idle"; yield return SampleFor(.3f);
            Assert.That(Vector3.ProjectOnPlane(player.Controller.velocity,Vector3.up).magnitude,Is.LessThan(.02f),"No residual fixture or collision momentum before input.");
            phase="player-start"; Keys(Key.W); yield return SampleFor(.6f);
            phase="player-steady"; yield return SampleFor(directory.Contains("Fine")||directory.Contains("Final")||directory.Contains("VisualBaseline") ? .25f : .5f);
            if(directory.Contains("Final")) Assert.That(player.GetComponent<PlayerLocomotionPresenter>().NormalizedSpeed,Is.GreaterThan(.9f),"A collision-stalled trial is not a valid responsiveness result.");
            phase="player-stop"; Keys(); yield return SampleFor(.5f);
            Place(new Vector3(-2.5f,.03f,.6f));
            phase="player-forward"; Keys(Key.W); yield return SampleFor(.55f);
            phase="player-reverse"; Keys(Key.S); yield return SampleFor(.65f);
            phase="player-turn90"; Keys(Key.D); yield return SampleFor(.55f);
            phase="player-turn180"; Keys(Key.A); yield return SampleFor(.65f);
            phase="player-wasd";
            foreach(var k in new[]{Key.W,Key.A,Key.S,Key.D}) { Keys(k); yield return SampleFor(.18f); }
            phase="player-diagonal"; Keys(Key.W,Key.D); yield return SampleFor(.4f);
            phase="player-wave"; Assert.That(player.GetComponent<PlayerEmoteController>().TryWave(),Is.True);
            yield return SampleFor(.7f); Keys(); yield return SampleFor(.4f);
            phase="player-modal"; player.SetDialogueInputSuspended(true); Keys(Key.W); yield return SampleFor(.15f);
            Assert.That(Vector3.ProjectOnPlane(player.Controller.velocity,Vector3.up).magnitude,Is.LessThan(.02f));
            Keys(); player.SetDialogueInputSuspended(false);
            Place(new Vector3(-2.5f,.03f,.6f));
            var wall=GameObject.CreatePrimitive(PrimitiveType.Cube); wall.name="V2PolishTemporaryWall";
            wall.transform.position=player.transform.position+Vector3.forward*.9f+Vector3.up;
            wall.transform.localScale=new Vector3(3,2,.3f); Physics.SyncTransforms();
            phase="player-wall"; Keys(Key.W); yield return SampleFor(1f);
            Assert.That(player.GetComponent<PlayerLocomotionPresenter>().NormalizedSpeed,Is.LessThan(.05f));
            Keys(); Object.Destroy(wall); phase="player-wall-stop"; yield return SampleFor(.35f);
            Flush();
        }
        private IEnumerator NpcSequence()
        {
            Place(new Vector3(-2f,.03f,.4f));
            int index=0;
            foreach(var point in new[]{ new Vector3(.9f,0,4.5f), new Vector3(1.9f,0,4.5f),new Vector3(1.9f,0,5.4f),home })
            {
                phase="npc-path-"+(index++);
                Assert.That(NavMesh.SamplePosition(point,out NavMeshHit hit,.5f,agent.areaMask),Is.True);
                File.AppendAllText(Path.Combine(directory,"waypoints.txt"),phase+" requested="+point+" sampled="+hit.position+"\n");
                Assert.That(agent.SetDestination(hit.position),Is.True); agent.isStopped=false;
                int frames=0; float until=Time.time+12f;
                do { yield return null; Record(); frames++; }
                while((agent.pathPending || agent.remainingDistance>agent.stoppingDistance+.08f || agent.velocity.magnitude>.05f) && Time.time<until);
                Flush(); Assert.That(agent.pathStatus,Is.EqualTo(NavMeshPathStatus.PathComplete));
                Assert.That(Vector3.Distance(agent.transform.position,hit.position),Is.LessThan(agent.stoppingDistance+.08f),"Sampled waypoint must remain reachable.");
                phase="npc-stop"; yield return SampleFor(.3f);
            }
            agent.ResetPath();
        }
        private IEnumerator Chain()
        {
            phase="chain-admit";
            var routine=merchant.GetComponent<NpcDefaultRoutineController>(); routine.enabled=true;
            Place(new Vector3(-2f,.03f,.4f));
            merchant.transform.rotation=Quaternion.LookRotation(Vector3.ProjectOnPlane(cup.GetComponent<PerceivableEntity>().VisualAnchor.position-merchant.transform.position,Vector3.up));
            var driver=merchant.GetComponent<CognitiveRunDriver>(); driver.enabled=false;
            merchant.GetComponent<AgentOrchestrator>().CancelCurrentRun();
            merchant.GetComponent<VisionSensor>().ScanNow();
            var world=Object.FindAnyObjectByType<WorldStateService>(); int revision=world.WorldRevision;
            GoalOutcome? outcome=null; merchant.GetComponent<GoalPlanRunner>().GoalOutcomePublished+=o=>outcome=o;
            var ev=new AgentEvent(Guid.NewGuid().ToString("N"),AgentEventTypes.PlayerUtterance,playerEntity.EntityId,merchant.EntityId,"",
                new Dictionary<string,string>{{AgentEventPayloadKeys.Text,"把这个杯子拿给我"}},Time.realtimeSinceStartupAsDouble,revision);
            Assert.That(merchant.GetComponent<AgentEventReceiver>().Receive(ev),Is.True);
            var request=merchant.GetComponent<CognitiveContextComposer>().LastRequest;
            Assert.That(request,Is.Not.Null); driver.enabled=true; Assert.That(driver.TryOwnRequest(request),Is.True);
            var response=new CognitiveResponse { message_type="cognitive_response",schema_version="1.0",request_id=request.request_id,
                trace_id=request.trace_id,run_id=request.run.run_id,agent_entity_id=merchant.EntityId,
                decision=new CognitiveDecision { decision_kind=CognitiveDecisionKinds.ActionProposal,utterance="我去拿杯子。",emotion="neutral",gesture="none",
                    action_proposal=new CognitiveActionProposal { action_id="bring_object",bindings=new Dictionary<string,string>{{"target",cup.EntityId},{"destination",playerEntity.EntityId}} } },
                validated_goal=new GoalProposal { type="bring_object",target_entity_id=cup.EntityId,destination_entity_id=playerEntity.EntityId } };
            Assert.That(driver.TryConsumeResponse(response),Is.True);
            int frames=0; bool picked=false, carried=false; float until=Time.time+40f;
            do
            {
                phase="chain-"+(cup.GetComponent<HeldItemState>().HolderEntityId??"unheld");
                yield return null; Record(); frames++;
                picked |= cup.GetComponent<HeldItemState>().HolderEntityId==merchant.EntityId;
                carried |= picked && merchant.GetComponent<CarryPresenter>().CurrentWeight>.8f && agent.velocity.magnitude>.1f;
                if(cup.GetComponent<HeldItemState>().HolderEntityId==merchant.EntityId)
                {
                    Assert.That(world.WorldRevision,Is.EqualTo(revision+1));
                    Assert.That(cup.transform.parent,Is.SameAs(merchant.transform.Find("HandHoldPoint")));
                    Assert.That(merchant.GetComponent<MotionChannelCoordinator>().ActiveGestureId,Is.Zero);
                    Assert.That(merchant.GetComponentInChildren<HeadGazeController>().CurrentWeight,Is.Zero);
                    Assert.That(merchant.GetComponentInChildren<DialogueAnimationPresenter>().CurrentWeight,Is.Zero);
                    Assert.That(merchant.GetComponentInChildren<FacingController>().IsDialogueFacingActive,Is.False);
                }
            } while(driver.HasPendingActionOutcome && Time.time<until);
            Flush(); Assert.That(driver.HasPendingActionOutcome,Is.False);
            Assert.That(outcome.HasValue,Is.True); Assert.That(outcome.Value.Status,Is.EqualTo(PlanExecutionStatus.Succeeded));
            Assert.That(picked && carried,Is.True);
            Assert.That(cup.GetComponent<HeldItemState>().HolderEntityId,Is.EqualTo(playerEntity.EntityId));
            Assert.That(world.WorldRevision,Is.EqualTo(revision+2));
            Assert.That(cup.transform.parent,Is.SameAs(player.transform.Find("PlayerHoldPoint")));
            Assert.That(cup.GetComponentsInChildren<Renderer>(true).All(r=>!r.enabled),Is.True);
            Assert.That(cup.GetComponent<Collider>().enabled,Is.False);
            Assert.That(cup.GetComponent<PerceivableEntity>().isActiveAndEnabled,Is.False);
            // Observe the normal eight-second hold; do not shorten its admission contract.
            Assert.That(routine.IsEngagementHeld,Is.True,"The real successful outcome must refresh engagement.");
            Vector3 delivery=merchant.transform.position;
            phase="chain-engagement"; yield return SampleFor(.5f);
            Assert.That(Vector3.Distance(delivery,merchant.transform.position),Is.LessThan(.15f));
            frames=0; until=Time.realtimeSinceStartup+12f;
            while(routine.IsEngagementHeld && Time.realtimeSinceStartup<until) { yield return null; Record(); frames++; }
            Assert.That(routine.IsEngagementHeld,Is.False);
            phase="chain-return"; frames=0; until=Time.time+20f;
            var returnHome=merchant.GetComponent<ReturnToHomeRoutine>();
            while((Vector3.Distance(merchant.transform.position,home)>.22f || Quaternion.Angle(merchant.transform.rotation,returnHome.HomeRotation)>1.1f) && Time.time<until)
            { yield return null; Record(); frames++; }
            Flush(); Assert.That(Vector3.Distance(merchant.transform.position,home),Is.LessThan(.22f));
            Assert.That(Quaternion.Angle(merchant.transform.rotation,returnHome.HomeRotation),Is.LessThan(1.1f));
            phase="chain-idle"; yield return SampleFor(.5f);
            File.WriteAllText(Path.Combine(directory,"chain-result.json"),"{\"path\":\"PASS\",\"action\":\"PASS\",\"ownership\":\"PASS\",\"return\":\"PASS\"}"); Flush();
        }
        private IEnumerator Trial()
        {
            yield return PlayerSequence(); yield return NpcSequence(); yield return Chain();
        }

        private IEnumerator FocusSequence()
        {
            // Clear counter-front floor, short travel to keep both feet visible throughout stopping.
            Place(new Vector3(0,.03f,1f)); phase="player-idle"; yield return SampleFor(.3f);
            Assert.That(Vector3.ProjectOnPlane(player.Controller.velocity,Vector3.up).magnitude,Is.LessThan(.02f));
            phase="player-start"; Keys(Key.W); yield return SampleFor(.45f);
            phase="player-steady"; yield return SampleFor(.05f);
            Assert.That(player.GetComponent<PlayerLocomotionPresenter>().NormalizedSpeed,Is.GreaterThan(.9f));
            phase="player-stop"; Keys(); yield return SampleFor(.45f);
            Assert.That(player.GetComponent<PlayerLocomotionPresenter>().NormalizedSpeed,Is.LessThan(.02f));
            phase="player-turn90"; Keys(Key.D); yield return SampleFor(.35f);
            phase="player-turn180"; Keys(Key.A); yield return SampleFor(.45f);
            phase="player-wave"; Keys(Key.W); Assert.That(player.GetComponent<PlayerEmoteController>().TryWave(),Is.True);
            yield return SampleFor(.35f); Keys(); phase="player-focus-stop"; yield return SampleFor(.35f); Flush();
        }

        [UnityTest] public IEnumerator Baseline() => Trial();
        [UnityTest] public IEnumerator Smooth() => Trial();
        [UnityTest] public IEnumerator Responsive() => Trial();
        [UnityTest] public IEnumerator Balanced() => Trial();
        [UnityTest] public IEnumerator FineBaseline() => Trial();
        [UnityTest] public IEnumerator FineMid() => Trial();
        [UnityTest] public IEnumerator FineLow() => Trial();
        [UnityTest] public IEnumerator FineResponse() => Trial();
        [UnityTest] public IEnumerator VisualBaseline() => Trial();
        [UnityTest] public IEnumerator Final30() => Trial();
        [UnityTest] public IEnumerator Final60() => Trial();
        [UnityTest] public IEnumerator Final120() => Trial();
        [UnityTest] public IEnumerator FinalFocus() => FocusSequence();
        [UnityTest] public IEnumerator VisualBaselineFocus() => FocusSequence();
    }
}
