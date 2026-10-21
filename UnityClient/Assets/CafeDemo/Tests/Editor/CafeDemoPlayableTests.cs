using System.Collections;
using System;
using System.Collections.Generic;
using System.Linq;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Input;
using EmbodiedNPC.Dialogue.Presentation;
using EmbodiedNPC.Events;
using EmbodiedNPC.Identity;
using EmbodiedNPC.Interaction;
using EmbodiedNPC.Memory;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Settings;
using EmbodiedNPC.UI;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    // Editor test runner enters real Play Mode; no Build Settings or core gameplay fixtures needed.
    public sealed class CafeDemoPlayableTests
    {
        private const string ScenePath = "Assets/Scenes/CafeDemoV2.unity";
        private CafeDemoPlayerController player;
        private CafeDemoOrbitCamera orbit;
        private Keyboard keyboard;
        private Mouse mouse;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            EditorSceneManager.OpenScene(ScenePath);
            string endpoint = Environment.GetEnvironmentVariable("EMBODIEDNPC_TEST_BACKEND_URL");
            if (!string.IsNullOrWhiteSpace(endpoint))
                typeof(BackendWebSocketClient).GetField("serverUrl", System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic).SetValue(
                    Object.FindAnyObjectByType<BackendWebSocketClient>(), endpoint);
            // These tests supply a configured scene; do not let the startup GET override that fixture.
            Object.FindAnyObjectByType<FirstRunModelSetup>(FindObjectsInactive.Include).enabled = false;
            yield return new EnterPlayMode();
            // Let the actual Cafe startup owner run Start before supplying this gameplay fixture.
            yield return null;
            yield return null;
            var setup = Object.FindAnyObjectByType<FirstRunModelSetup>(FindObjectsInactive.Include);
            Assert.That(setup.enabled, Is.False);
            var settings = Object.FindAnyObjectByType<CafeDemoDeveloperPanel>();
            settings.ApplyStartupConfig(new ModelConfigView { configured = true });
            settings.Close();
            player=Object.FindAnyObjectByType<CafeDemoPlayerController>();
            orbit=Object.FindAnyObjectByType<CafeDemoOrbitCamera>();
            keyboard=InputSystem.AddDevice<Keyboard>("CafeDemoTestKeyboard");
            mouse=InputSystem.AddDevice<Mouse>("CafeDemoTestMouse");
            Assert.That(player,Is.Not.Null);Assert.That(orbit,Is.Not.Null);
            yield return null;yield return null;
            Assert.That(settings.IsOpen, Is.False);
            Assert.That(setup.gameObject.activeInHierarchy, Is.False,
                "The reused Developer form is hidden with its parent panel.");
            Assert.That(Object.FindAnyObjectByType<DialogueController>().IsSetupReady, Is.True);
        }
        [UnityTearDown]
        public IEnumerator TearDown()
        {
            foreach(var device in InputSystem.devices.Where(d=>d.name.StartsWith("CafeDemoTest")).ToArray())InputSystem.RemoveDevice(device);
            if(Application.isPlaying)yield return new ExitPlayMode();
        }
        [UnityTest]
        public IEnumerator WorldSignage_UsesSingleSidedDepthTested3DTextAndSceneReferencesRemainValid()
        {
            foreach (string name in new[] { "ExitText", "MenuTitle", "MenuItems" })
            {
                var root = GameObject.Find(name);
                Assert.That(root.GetComponent<TextMesh>(), Is.Null, name);
                var text = root.GetComponent<TMPro.TextMeshPro>();
                Assert.That(text, Is.Not.Null, name);
                Assert.That(text.font, Is.Not.Null);
                Assert.That(text.isOverlay, Is.False);
                Assert.That(text.enableCulling, Is.True);
                Assert.That(text.fontSharedMaterial.shader.name, Does.Not.Contain("Overlay"));
                Assert.That(text.fontSharedMaterial.GetFloat("_ZTestMode"),
                    Is.EqualTo((float)UnityEngine.Rendering.CompareFunction.LessEqual));
                Assert.That(text.fontSharedMaterial.GetFloat("_CullMode"),
                    Is.EqualTo((float)UnityEngine.Rendering.CullMode.Back));
            }
            foreach (var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
                foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                    Assert.That(UnityEditor.GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transform.gameObject),
                        Is.Zero, transform.name);
            Assert.That(Camera.main, Is.Not.Null);
            yield return null;
        }

        private void Place(Vector3 position)
        {
            player.Controller.enabled=false;player.transform.position=position;player.Controller.enabled=true;Physics.SyncTransforms();
        }
        private IEnumerator KeyFor(Key key,float seconds)
        {
            InputSystem.QueueStateEvent(keyboard,new KeyboardState(key));
            // Let the queued state reach the gameplay action before starting the hold timer.
            yield return null;
            yield return Wait(seconds);
            InputSystem.QueueStateEvent(keyboard,new KeyboardState());yield return null;
        }
        // EditMode runner advances EnterPlayMode tests itself; wait on the simulation clock,
        // not WaitForSeconds, which this runner can resume without honoring the duration.
        private IEnumerator Wait(float seconds)
        { float until=Time.time+seconds;while(Time.time<until)yield return null; }
        private IEnumerator MouseEvent(Vector2 delta,float scroll=0,bool right=true)
        {
            var state=new MouseState{delta=delta,scroll=new Vector2(0,scroll)};
            if(right)state=state.WithButton(MouseButton.Right);
            InputSystem.QueueStateEvent(mouse,state);yield return null;yield return null;
            InputSystem.QueueStateEvent(mouse,new MouseState());yield return null;
        }
        private IEnumerator Aim(float yaw,float pitch)
        {yield return MouseEvent(new Vector2(Mathf.DeltaAngle(orbit.Yaw,yaw)/.18f,(orbit.Pitch-pitch)/.18f));}

        private void SettingsEscape(CafeDemoDeveloperPanel settings, bool pressed)
        {
            InputSystem.QueueStateEvent(keyboard, pressed ? new KeyboardState(Key.Escape) : new KeyboardState());
            InputSystem.Update();
            // The EditMode batch runner advances synthetic input after the player Update loop.
            settings.SendMessage("Update");
        }

        [UnityTest]
        public IEnumerator CafeVisiblePlayerAndCup_ReachObserverLocalCognitiveRequest()
        {
            WorldEntity merchant = Object.FindObjectsByType<WorldEntity>(FindObjectsInactive.Exclude)
                .Single(value => value.EntityId == "npc_cafe_01");
            WorldEntity customer = Object.FindObjectsByType<WorldEntity>(FindObjectsInactive.Exclude)
                .Single(value => value.EntityId == "npc_cafe_regular_01");
            Assert.That(customer.GetComponent<VisionSensor>(), Is.Not.Null,
                "The conversational customer needs an observer-local visual source.");
            Place(new Vector3(-.8f, .02f, 2f));
            yield return Wait(.1f);
            VisionSensor sensor = merchant.GetComponent<VisionSensor>();
            Assert.That(sensor, Is.Not.Null);
            sensor.ScanNow();
            Assert.That(sensor.CurrentVisibleEntityIds, Does.Contain("player_01"));
            Assert.That(sensor.CurrentVisibleEntityIds, Does.Contain("cup_01"));

            var trigger = new CognitiveTrigger("cafe_visible_trigger", CognitiveTriggerTypes.Event,
                merchant.EntityId, null, 1d, AgentEventTypes.EntityBecameVisible);
            var run = new CognitiveRun("cafe_visible_run", merchant.EntityId,
                trigger.TriggerId, trigger.TriggerType, 1d);
            CognitiveContextComposer composer = merchant.GetComponent<CognitiveContextComposer>();
            Assert.That(composer.TryBuildRequest(run, trigger, out CognitiveRequest request), Is.True);
            Assert.That(request.context.perception.entities.Select(entity => entity.entity_id),
                Is.SupersetOf(new[] { "player_01", "cup_01" }));
        }

        [UnityTest]
        public IEnumerator VisualReengagement_ProjectsObservationParentAndReusesEventTraceInRequest()
        {
            WorldEntity merchant = Object.FindObjectsByType<WorldEntity>(FindObjectsInactive.Exclude)
                .Single(value => value.EntityId == "npc_cafe_01");
            WorldEntity playerEntity = player.GetComponent<WorldEntity>();
            VisualObservationAgentEventBridge visualBridge =
                merchant.GetComponent<VisualObservationAgentEventBridge>();
            AgentEventReceiver receiver = merchant.GetComponent<AgentEventReceiver>();
            CognitiveContextComposer composer = merchant.GetComponent<CognitiveContextComposer>();
            Assert.That(visualBridge, Is.Not.Null);
            Assert.That(playerEntity.IsAgent(AgentRole.Player), Is.True);
            Assert.That(Object.FindAnyObjectByType<EntityRegistry>().TryGetEntity(playerEntity.EntityId,
                out _), Is.True);
            visualBridge.enabled = false;
            visualBridge.enabled = true;
            var fields = new Dictionary<string, string> { ["visibility_state"] = "current_visible" };
            var observation = new Observation("visual_r3c_01", merchant.EntityId,
                ObservationSourceType.ContinuousScan, "visual:" + playerEntity.EntityId,
                ObservationChannel.Visual, playerEntity.EntityId, fields, Array.Empty<string>(),
                1f, 1, Time.unscaledTimeAsDouble + 60d, double.PositiveInfinity);
            Assert.That(observation.TraceId, Is.Null);

            Assert.That(visualBridge.TryProject(new ObservationApplyResult(ObservationApplyCode.Applied,
                observation, 1), out AgentEvent agentEvent), Is.True);
            Assert.That(agentEvent.SourceObservationId, Is.EqualTo(observation.ObservationId));
            Assert.That(agentEvent.TraceId, Is.Not.Empty);
            receiver.Receive(agentEvent);
            CognitiveTrigger trigger = new("trigger_r3c", CognitiveTriggerTypes.Event, merchant.EntityId,
                agentEvent.EventId, 1d, agentEvent.EventType);
            CognitiveRun run = new("run_r3c", merchant.EntityId, trigger.TriggerId, trigger.TriggerType, 1d);

            Assert.That(composer.TryBuildRequest(run, trigger, out CognitiveRequest request), Is.True);
            Assert.That(request.trace_id, Is.EqualTo(agentEvent.TraceId));
            Assert.That(request.context.current_event.event_id, Is.EqualTo(agentEvent.EventId));
            Assert.That(request.context.current_event.source_observation_id,
                Is.EqualTo(observation.ObservationId));
            Assert.That(request.request_id, Is.Not.EqualTo(request.trace_id));
            Assert.That(run.RunId, Is.Not.EqualTo(request.trace_id));
            yield return null;
        }

        [UnityTest]
        public IEnumerator DirectUtterance_OwnsEventTraceReusedByRequest()
        {
            CognitiveDialogueSubmissionHandler customer = DialogueHandler("npc_cafe_regular_01");
            AgentOrchestrator orchestrator = customer.GetComponent<AgentOrchestrator>();
            CognitiveContextComposer composer = customer.GetComponent<CognitiveContextComposer>();
            float deadline = Time.realtimeSinceStartup + 5f;
            while (orchestrator.HasActiveRun && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(orchestrator.HasActiveRun, Is.False);

            DialogueSubmissionResult result = customer.Submit(new UserUtterance
            {
                conversationId = "r3c_direct_utterance",
                speakerEntityId = "player_01",
                targetAgentId = "npc_cafe_regular_01",
                inputMode = "text",
                text = "你好",
            });
            Assert.That(result.Accepted, Is.True, result.Status);
            AgentEvent agentEvent = customer.LastSubmittedEvent;
            CognitiveRequest request = composer.LastRequest;
            Assert.That(agentEvent, Is.Not.Null);
            Assert.That(request, Is.Not.Null);
            Assert.That(agentEvent.TraceId, Is.Not.Empty);
            Assert.That(agentEvent.SourceObservationId, Is.Null);
            Assert.That(request.trace_id, Is.EqualTo(agentEvent.TraceId));
            Assert.That(request.context.current_event.event_id, Is.EqualTo(agentEvent.EventId));
            Assert.That(result.AcceptedTurn.TraceId, Is.EqualTo(agentEvent.TraceId));
            Assert.That(request.request_id, Is.Not.EqualTo(request.trace_id));
            Assert.That(request.run.run_id, Is.Not.EqualTo(request.trace_id));
            yield return null;
        }

        [UnityTest]
        public IEnumerator AgentFeatureProfiles_DeclareExistingCompositionAndKeepActionCapabilitiesSeparate()
        {
            WorldEntity[] entities = Object.FindObjectsByType<WorldEntity>(FindObjectsInactive.Exclude);
            WorldEntity background = entities.Single(value => value.EntityId == "npc_cafe_background_01");
            WorldEntity customer = entities.Single(value => value.EntityId == "npc_cafe_regular_01");
            WorldEntity merchant = entities.Single(value => value.EntityId == "npc_cafe_01");

            AgentFeatureProfileBinding backgroundFeatures = background.GetComponent<AgentFeatureProfileBinding>();
            AgentFeatureProfileBinding customerFeatures = customer.GetComponent<AgentFeatureProfileBinding>();
            AgentFeatureProfileBinding merchantFeatures = merchant.GetComponent<AgentFeatureProfileBinding>();

            Assert.That(backgroundFeatures, Is.Not.Null);
            Assert.That(customerFeatures, Is.Not.Null);
            Assert.That(merchantFeatures, Is.Not.Null);
            Assert.That(backgroundFeatures.Configuration.CognitionEnabled, Is.False);
            Assert.That(customerFeatures.Configuration.CognitionEnabled, Is.True);
            Assert.That(merchantFeatures.Configuration.CognitionEnabled, Is.True);
            Assert.That(backgroundFeatures.TryValidateConfiguration(out string backgroundError), Is.True,
                backgroundError);
            Assert.That(customerFeatures.TryValidateConfiguration(out string customerError), Is.True,
                customerError);
            Assert.That(merchantFeatures.TryValidateConfiguration(out string merchantError), Is.True,
                merchantError);
            Assert.That(background.GetComponent<AgentOrchestrator>(), Is.Null);
            Assert.That(background.GetComponent<CognitiveRunDriver>(), Is.Null);

            AgentActionCapabilityProfile customerActions = customer.GetComponent<AgentActionCapabilityProfile>();
            AgentActionCapabilityProfile merchantActions = merchant.GetComponent<AgentActionCapabilityProfile>();
            Assert.That(customerActions.TryBuildManifest(out CognitiveCapabilityManifestDto customerManifest), Is.True);
            Assert.That(merchantActions.TryBuildManifest(out CognitiveCapabilityManifestDto merchantManifest), Is.True);
            Assert.That(customerManifest.available_actions, Is.Empty);
            CharacterDefinition customerCharacter = customer.GetComponent<CognitiveContextComposer>().CharacterDefinition;
            Assert.That(customerCharacter, Is.Not.Null);
            Assert.That(customerCharacter.CharacterId, Is.EqualTo("cafe.regular_customer"));
            Assert.That(customerCharacter.RoleSummary, Is.Not.Empty);
            Assert.That(merchantManifest.available_actions.Select(value => value.action_id),
                Does.Contain("bring_object"));
            yield return null;
        }

        [UnityTest]
        public IEnumerator RuntimeV2PresentedDialogue_AppendsRegularCustomerInputAndResponse()
        {
            BackendWebSocketClient backend = Object.FindAnyObjectByType<BackendWebSocketClient>();
            DialogueController controller = Object.FindAnyObjectByType<DialogueController>();
            TextUtteranceInput input = Object.FindAnyObjectByType<TextUtteranceInput>();
            CognitiveDialogueSubmissionHandler customer = DialogueHandler("npc_cafe_regular_01");
            AgentOrchestrator orchestrator = customer.GetComponent<AgentOrchestrator>();
            CognitiveRunDriver driver = customer.GetComponent<CognitiveRunDriver>();
            var results = new List<ValidatedExperienceAppendResult>();
            var presentations = new List<PresentedCognitiveUtterance>();
            backend.ValidatedExperienceAppendResultReceived += results.Add;
            driver.UtterancePresented += presentations.Add;

            float connectionDeadline = Time.realtimeSinceStartup + 5f;
            while (!backend.IsConnected && Time.realtimeSinceStartup < connectionDeadline) yield return null;
            Assert.That(backend.IsConnected, Is.True);
            float drainDeadline = Time.realtimeSinceStartup + 5f;
            while (backend.PendingExperienceCount > 0 && Time.realtimeSinceStartup < drainDeadline) yield return null;
            results.Clear();

            Place(customer.transform.position + Vector3.right * .5f);
            yield return Wait(.1f);
            Assert.That(controller.SelectTarget(customer.GetComponent<DialogueParticipant>()), Is.True);
            string conversationId = controller.CurrentConversationId;
            Assert.That(input.SubmitText("memory-integration-a-customer"), Is.True);
            controller.HideDialoguePresentation();
            Assert.That(controller.IsPresentationVisible, Is.False);
            Assert.That(controller.CurrentConversationId, Is.EqualTo(conversationId));

            AcceptedDialogueTurn accepted = customer.LastAcceptedTurn;
            Assert.That(accepted, Is.Not.Null);
            Assert.That(accepted.SpeakerEntityId, Is.EqualTo("player_01"));
            Assert.That(accepted.TargetAgentId, Is.EqualTo("npc_cafe_regular_01"));
            Assert.That(accepted.SourceEventId, Is.EqualTo(customer.LastSubmittedEvent.EventId));
            Assert.That(customer.LastSubmittedEvent.TraceId, Is.Not.Empty);
            Assert.That(customer.LastSubmittedEvent.SourceObservationId, Is.Null);
            Assert.That(accepted.TraceId, Is.EqualTo(customer.LastSubmittedEvent.TraceId));
            Assert.That(customer.GetComponent<CognitiveContextComposer>().LastRequest.trace_id,
                Is.EqualTo(customer.LastSubmittedEvent.TraceId));
            Assert.That(customer.GetComponent<CognitiveContextComposer>().LastRequest.context.current_event.event_id,
                Is.EqualTo(customer.LastSubmittedEvent.EventId));
            Assert.That(accepted.RequestId, Is.Not.EqualTo(accepted.TraceId));
            Assert.That(accepted.RequestId, Does.StartWith("cognitive-request-"));
            float responseDeadline = Time.realtimeSinceStartup + 5f;
            while ((orchestrator.LastRun == null || !orchestrator.LastRun.IsTerminal) &&
                   Time.realtimeSinceStartup < responseDeadline) yield return null;
            Assert.That(orchestrator.LastRun, Is.Not.Null);
            Assert.That(orchestrator.LastRun.State, Is.EqualTo(CognitiveRunState.Completed));
            Assert.That(presentations, Has.Count.EqualTo(1));
            Assert.That(presentations[0].OwnerAgentEntityId, Is.EqualTo("npc_cafe_regular_01"));
            Assert.That(presentations[0].PlayerEntityId, Is.EqualTo("player_01"));
            Assert.That(presentations[0].ConversationId, Is.EqualTo(conversationId));
            Assert.That(presentations[0].RequestId, Is.EqualTo(accepted.RequestId));
            Assert.That(presentations[0].TraceId, Is.EqualTo(accepted.TraceId));
            Assert.That(presentations[0].SourceEventId, Is.EqualTo(accepted.SourceEventId));
            DialogueBubbleManager bubbles = Object.FindAnyObjectByType<DialogueBubbleManager>();
            Assert.That(bubbles.GetMessages(customer.GetComponent<WorldEntity>().EntityId).Count,
                Is.GreaterThan(0));

            float appendDeadline = Time.realtimeSinceStartup + 10f;
            while (results.Count(value => value.ObserverId == "npc_cafe_regular_01") < 2 &&
                   Time.realtimeSinceStartup < appendDeadline) yield return null;
            ValidatedExperienceAppendResult[] customerAppends = results.Where(
                value => value.ObserverId == "npc_cafe_regular_01").ToArray();
            Assert.That(customerAppends, Has.Length.GreaterThanOrEqualTo(2),
                $"Input/response ACKs missing; pending={backend.PendingExperienceCount}; status={backend.ExperienceWriteStatus}");
            Assert.That(customerAppends.All(value => value.IsPersisted), Is.True);
            Assert.That(results.Any(value => value.ObserverId == "npc_cafe_01"), Is.False,
                "The customer dialogue must not be appended to the Merchant observer stream.");
            Assert.That(controller.SelectTarget(customer.GetComponent<DialogueParticipant>()), Is.True);
            Assert.That(controller.CurrentConversationId, Is.EqualTo(conversationId));
            DialogueHistoryPanel history = Object.FindAnyObjectByType<DialogueHistoryPanel>();
            history.Toggle();
            float historyDeadline = Time.realtimeSinceStartup + 10f;
            while ((!history.DisplayText.Contains("memory-integration-a-customer") ||
                    !history.DisplayText.Contains("NPC: ")) && Time.realtimeSinceStartup < historyDeadline)
                yield return null;
            Assert.That(history.DisplayText, Does.Contain("memory-integration-a-customer"));
            Assert.That(history.DisplayText, Does.Contain("NPC: "));
            driver.UtterancePresented -= presentations.Add;
            backend.ValidatedExperienceAppendResultReceived -= results.Add;
        }

        [UnityTest]
        public IEnumerator MerchantGoalOutcome_HasExistingObserverLocalExperienceSubscriber()
        {
            CognitiveDialogueSubmissionHandler merchant = DialogueHandler("npc_cafe_01");
            GoalPlanRunner runner = merchant.GetComponent<GoalPlanRunner>();
            var eventField = typeof(GoalPlanRunner).GetField("GoalOutcomePublished",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var subscribers = eventField?.GetValue(runner) as Delegate;

            Assert.That(runner, Is.Not.Null);
            Assert.That(merchant.GetComponent<PerceivedStateStore>(), Is.Not.Null);
            Assert.That(subscribers, Is.Not.Null);
            Assert.That(subscribers.GetInvocationList().Any(value =>
                value.Target is GoalOutcomeExperienceAdapter), Is.True,
                "Merchant GoalOutcomePublished must reuse GoalOutcomeExperienceAdapter.");
            yield return null;
        }

        [UnityTest]
        public IEnumerator PlayerAndNpc_HaveRuntimeV2EntityIdentity()
        {
            WorldEntity playerEntity = player.GetComponent<WorldEntity>();
            WorldEntity npcEntity = Object.FindAnyObjectByType<CafeDemoNavMeshTestAgent>().GetComponent<WorldEntity>();

            Assert.That(playerEntity, Is.Not.Null);
            Assert.That(playerEntity.EntityId, Is.EqualTo("player_01"));
            Assert.That(playerEntity.DefinitionId, Is.EqualTo("character.player.default"));
            Assert.That(playerEntity.HasExplicitDefinitionId, Is.True);
            Assert.That(playerEntity.EntityKind, Is.EqualTo(EntityKind.Agent));
            Assert.That(playerEntity.AgentRole, Is.EqualTo(AgentRole.Player));
            LocalTestIdentityProvider provider = player.GetComponent<LocalTestIdentityProvider>();
            PlayerIdentityBinding binding = player.GetComponent<PlayerIdentityBinding>();
            EntityRegistry registry = Object.FindAnyObjectByType<EntityRegistry>();
            Assert.That(provider, Is.Not.Null);
            Assert.That(binding, Is.Not.Null);
            Assert.That(registry, Is.Not.Null);
            Assert.That(provider.TryGetCurrentIdentity(out PlayerIdentitySnapshot snapshot), Is.True);
            Assert.That(snapshot.PlayerUid, Is.EqualTo("cafe-local-player-001"));
            Assert.That(snapshot.DisplayName, Is.EqualTo("Player"));
            Assert.That(snapshot.ProfileRevision, Is.EqualTo(1));
            Assert.That(playerEntity.TryGetPersistentIdentityKey(out PersistentIdentityKey key), Is.True);
            Assert.That(key.ToString(), Is.EqualTo("player:Player:cafe-local-player-001"));
            Assert.That(registry.IsReady, Is.True);
            Assert.That(registry.TryResolvePersistentIdentity(key, out WorldEntity resolved), Is.True);
            Assert.That(resolved, Is.SameAs(playerEntity));
            Assert.That(npcEntity, Is.Not.Null);
            Assert.That(npcEntity.EntityId, Is.EqualTo("npc_cafe_01"));
            Assert.That(npcEntity.DefinitionId, Is.EqualTo("character.npc.default"));
            Assert.That(npcEntity.HasExplicitDefinitionId, Is.True);
            Assert.That(npcEntity.EntityKind, Is.EqualTo(EntityKind.Agent));
            Assert.That(npcEntity.AgentRole, Is.EqualTo(AgentRole.Npc));
            Assert.That(npcEntity.TryGetPersistentIdentityKey(out PersistentIdentityKey merchantKey), Is.True);
            Assert.That(merchantKey.ToString(), Is.EqualTo("npc:Character:cafe.merchant.001"));
            Assert.That(registry.TryGetEntity("npc_cafe_regular_01", out WorldEntity regular), Is.True);
            Assert.That(regular.TryGetPersistentIdentityKey(out PersistentIdentityKey regularKey), Is.True);
            Assert.That(regularKey.ToString(), Is.EqualTo("npc:Character:cafe.regular_customer.001"));
            Assert.That(regularKey, Is.Not.EqualTo(merchantKey));
            Assert.That(registry.TryGetEntity("npc_cafe_background_01", out WorldEntity background), Is.True);
            Assert.That(background.HasPersistentIdentity, Is.False);
            Assert.That(registry.TryResolvePersistentIdentity(merchantKey, out WorldEntity merchantResolved), Is.True);
            Assert.That(merchantResolved, Is.SameAs(npcEntity));
            Assert.That(registry.TryResolvePersistentIdentity(regularKey, out WorldEntity regularResolved), Is.True);
            Assert.That(regularResolved, Is.SameAs(regular));
            yield return null;
        }

        [UnityTest]
        public IEnumerator LocalIdentityDisplayNameChange_KeepsUidAndEntityId()
        {
            WorldEntity entity = player.GetComponent<WorldEntity>();
            LocalTestIdentityProvider provider = player.GetComponent<LocalTestIdentityProvider>();
            PlayerIdentityBinding binding = player.GetComponent<PlayerIdentityBinding>();
            string entityId = entity.EntityId;
            Assert.That(entity.TryGetPersistentIdentityKey(out PersistentIdentityKey originalKey), Is.True);

            Assert.That(provider.TrySetDisplayName("Cafe Player", 2, DateTimeOffset.UtcNow), Is.True);
            Assert.That(binding.TryApplyIdentity(out string error), Is.True, error);

            Assert.That(binding.CurrentIdentity.DisplayName, Is.EqualTo("Cafe Player"));
            Assert.That(binding.CurrentIdentity.PlayerUid, Is.EqualTo("cafe-local-player-001"));
            Assert.That(entity.EntityId, Is.EqualTo(entityId));
            Assert.That(entity.TryGetPersistentIdentityKey(out PersistentIdentityKey refreshedKey), Is.True);
            Assert.That(refreshedKey, Is.EqualTo(originalKey));
            yield return null;
        }

        [UnityTest]
        public IEnumerator E6B_CounterCup_IsRegisteredPickupableReachableAndObserverLocal()
        {
            EntityRegistry registry = Object.FindAnyObjectByType<EntityRegistry>();
            Assert.That(registry.TryGetEntity("cup_01", out WorldEntity cup), Is.True);
            Assert.That(cup.name, Is.EqualTo("Cup_Prep"));
            Assert.That(cup.DefinitionId, Is.EqualTo("cafe.cup.coffee"));
            Assert.That(cup.EntityKind, Is.EqualTo(EntityKind.Object));
            Assert.That(cup.GetComponent<HeldItemState>().IsHeld, Is.False);
            Assert.That(cup.GetComponent<InteractableEntity>().SupportsPickUp, Is.True);
            InteractionAnchorProvider anchors = cup.GetComponent<InteractionAnchorProvider>();
            Assert.That(anchors.HasPickupAnchors, Is.True);

            CafeDemoNavMeshTestAgent merchant = Object.FindAnyObjectByType<CafeDemoNavMeshTestAgent>();
            NavMeshAgent agent = merchant.Agent;
            var filter = new NavMeshQueryFilter { agentTypeID = agent.agentTypeID, areaMask = agent.areaMask };
            Assert.That(NavMesh.SamplePosition(anchors.PickupApproachPoint.position,
                out NavMeshHit hit, .1f, filter), Is.True);
            var path = new NavMeshPath();
            Assert.That(NavMesh.CalculatePath(agent.transform.position, hit.position, filter, path), Is.True);
            Assert.That(path.status, Is.EqualTo(NavMeshPathStatus.PathComplete));

            EmbodiedNPC.Perception.ProximitySensor sensor =
                merchant.GetComponent<EmbodiedNPC.Perception.ProximitySensor>();
            sensor.ScanNow();
            Assert.That(sensor.CurrentNearbyEntityIds, Does.Contain("cup_01"));
            var snapshot = merchant.GetComponent<PerceptionContextProvider>().BuildSnapshot("把这个杯子拿给我");
            var perceivedCup = snapshot.ToPerceptionDto().entities.Single(entity => entity.entity_id == "cup_01");
            Assert.That(perceivedCup.kind, Is.EqualTo("item"));
            Assert.That(perceivedCup.capabilities, Does.Contain("pick_up"));

            GoalValidationResult admission = merchant.GetComponent<GoalValidator>().ValidateAdmission(
                new GoalProposal { type = "bring_object", target_entity_id = "cup_01", destination_entity_id = "player_01" });
            Assert.That(admission.IsApproved, Is.True, admission.Detail);
            yield return null;
        }

        [UnityTest]
        public IEnumerator E6B_AlreadyHeldCup_IsRejectedByExistingGoalAuthority()
        {
            EntityRegistry registry = Object.FindAnyObjectByType<EntityRegistry>();
            Assert.That(registry.TryGetEntity("cup_01", out WorldEntity cup), Is.True);
            CafeDemoNavMeshTestAgent merchant = Object.FindAnyObjectByType<CafeDemoNavMeshTestAgent>();
            HeldItemState held = cup.GetComponent<HeldItemState>();
            InteractionAnchorProvider anchors = cup.GetComponent<InteractionAnchorProvider>();
            Transform hand = merchant.transform.Find("HandHoldPoint");
            SkillResult pickup = Object.FindAnyObjectByType<WorldStateService>().TryPickUp(
                "test-pickup", "test-trace", merchant.GetComponent<WorldEntity>(), cup, held, hand, anchors.GrabPoint);
            Assert.That(pickup.Status, Is.EqualTo(SkillStatus.Succeeded));

            GoalValidationResult result = merchant.GetComponent<GoalValidator>().Validate(
                new GoalProposal { type = "bring_object", target_entity_id = "cup_01", destination_entity_id = "player_01" });

            Assert.That(result.IsApproved, Is.False);
            Assert.That(result.FailureCode, Is.EqualTo(GoalValidationFailureCode.TargetAlreadyHeld));
            yield return null;
        }

        [UnityTest]
        public IEnumerator E6B_UtteranceExecutesRealPickupGiveAndActionCompleted()
        {
            BackendWebSocketClient backend = Object.FindAnyObjectByType<BackendWebSocketClient>();
            CognitiveDialogueSubmissionHandler handler = DialogueHandler("npc_cafe_01");
            AgentOrchestrator orchestrator = handler.GetComponent<AgentOrchestrator>();
            GoalPlanRunner runner = handler.GetComponent<GoalPlanRunner>();
            CognitiveRunDriver driver = handler.GetComponent<CognitiveRunDriver>();
            CognitiveActionHandoff handoff = handler.GetComponent<CognitiveActionHandoff>();
            AgentEventReceiver receiver = handler.GetComponent<AgentEventReceiver>();
            EntityRegistry registry = Object.FindAnyObjectByType<EntityRegistry>();
            Assert.That(registry.TryGetEntity("cup_01", out WorldEntity cup), Is.True);
            HeldItemState held = cup.GetComponent<HeldItemState>();
            WorldStateService worldState = Object.FindAnyObjectByType<WorldStateService>();
            int startingWorldRevision = worldState.WorldRevision;
            GoalOutcome? outcome = null;
            runner.GoalOutcomePublished += value => outcome = value;

            float connectionDeadline = Time.realtimeSinceStartup + 5f;
            while (!backend.IsConnected && Time.realtimeSinceStartup < connectionDeadline) yield return null;
            Assert.That(backend.IsConnected, Is.True);
            Place(new Vector3(-.8f, .02f, 1.25f));
            yield return Wait(.1f);
            float setupDeadline = Time.realtimeSinceStartup + 5f;
            while (orchestrator.HasActiveRun && Time.realtimeSinceStartup < setupDeadline) yield return null;
            Assert.That(orchestrator.HasActiveRun, Is.False);

            VisionSensor merchantVision = handler.GetComponent<VisionSensor>();
            Assert.That(merchantVision, Is.Not.Null);
            Vector3 toCup = Vector3.ProjectOnPlane(
                cup.GetComponent<PerceivableEntity>().VisualAnchor.position - handler.transform.position,
                Vector3.up);
            handler.transform.rotation = Quaternion.LookRotation(toCup);
            Physics.SyncTransforms();
            merchantVision.ScanNow();
            Assert.That(merchantVision.CurrentVisibleEntityIds, Does.Contain("cup_01"),
                "The merchant must see the cup through the real FOV and LOS scan before utterance.");

            DialogueController controller = Object.FindAnyObjectByType<DialogueController>();
            TextUtteranceInput input = Object.FindAnyObjectByType<TextUtteranceInput>();
            Assert.That(controller.SelectTarget(handler.GetComponent<DialogueParticipant>()), Is.True);
            Assert.That(input.SubmitText("把这个杯子拿给我"), Is.True);
            Assert.That(handler.LastSubmittedEvent.TraceId, Is.Not.Empty);
            Assert.That(handler.GetComponent<CognitiveContextComposer>().LastRequest.trace_id,
                Is.EqualTo(handler.LastSubmittedEvent.TraceId));
            string conversationId = controller.CurrentConversationId;
            controller.HideDialoguePresentation();
            Assert.That(controller.IsPresentationVisible, Is.False);
            Assert.That(controller.CurrentConversationId, Is.EqualTo(conversationId));

            bool sawMerchantOwnership = false;
            bool sawWaitingActionResult = false;
            int merchantOwnershipRevision = -1;
            CognitiveRun actionRun = null;
            float executionDeadline = Time.realtimeSinceStartup + 30f;
            while ((!outcome.HasValue || runner.HasActiveTask) && Time.realtimeSinceStartup < executionDeadline)
            {
                CognitiveRun currentRun = orchestrator.CurrentRun;
                if (currentRun != null)
                {
                    actionRun ??= currentRun;
                    sawWaitingActionResult |= currentRun.State == CognitiveRunState.WaitingActionResult;
                }
                if (held.IsHeld && held.HolderEntityId == "npc_cafe_01")
                {
                    sawMerchantOwnership = true;
                    merchantOwnershipRevision = worldState.WorldRevision;
                }
                yield return null;
            }

            Assert.That(actionRun, Is.Not.Null);
            Assert.That(sawWaitingActionResult, Is.True,
                "ACTION_PROPOSAL handoff must keep the same CognitiveRun waiting for GoalOutcome.");
            Assert.That(sawMerchantOwnership, Is.True, "PickUp must authoritatively assign the cup to Merchant first.");
            Assert.That(merchantOwnershipRevision, Is.EqualTo(startingWorldRevision + 1));
            Assert.That(held.IsHeld, Is.True);
            Assert.That(held.HolderEntityId, Is.EqualTo("player_01"));
            Assert.That(cup.transform.parent.name, Is.EqualTo("PlayerHoldPoint"));
            Assert.That(cup.GetComponentsInChildren<Renderer>(true).All(r => !r.enabled), Is.True);
            Assert.That(cup.GetComponent<Collider>().enabled, Is.False);
            Assert.That(cup.GetComponent<PerceivableEntity>().isActiveAndEnabled, Is.False);
            Assert.That(cup.isActiveAndEnabled && cup.gameObject.activeInHierarchy && held.isActiveAndEnabled, Is.True);
            Assert.That(registry.TryGetEntity(cup.EntityId, out WorldEntity storedCup), Is.True);
            Assert.That(storedCup, Is.SameAs(cup));
            merchantVision.ScanNow();
            Assert.That(merchantVision.CurrentVisibleEntityIds, Does.Not.Contain(cup.EntityId));
            Assert.That(worldState.WorldRevision, Is.EqualTo(startingWorldRevision + 2));
            Assert.That(orchestrator.LastRun.State, Is.EqualTo(CognitiveRunState.Completed));
            Assert.That(orchestrator.LastRun, Is.SameAs(actionRun));
            Assert.That(orchestrator.LastRun.RunId, Is.EqualTo(actionRun.RunId));
            Assert.That(driver.HasPendingActionOutcome, Is.False);
            Assert.That(driver.LastValidatedGoalProposal.type, Is.EqualTo("bring_object"));
            Assert.That(handoff.LastProposalId, Is.Not.Empty);
            Assert.That(outcome.HasValue, Is.True);
            Assert.That(outcome.Value.Status, Is.EqualTo(PlanExecutionStatus.Succeeded));
            Assert.That(outcome.Value.GoalType, Is.EqualTo("bring_object"));
            Assert.That(outcome.Value.TraceId, Is.EqualTo(handler.LastSubmittedEvent.TraceId));
            Assert.That(outcome.Value.TargetEntityId, Is.EqualTo("cup_01"));
            Assert.That(outcome.Value.DestinationEntityId, Is.EqualTo("player_01"));
            Assert.That(receiver.LastEvent.EventType, Is.EqualTo(AgentEventTypes.ActionCompleted));
            Assert.That(receiver.LastEvent.TryGetPayload(AgentEventPayloadKeys.ActionId, out string action), Is.True);
            Assert.That(action, Is.EqualTo("bring_object"));
            Assert.That(receiver.LastEvent.TryGetPayload(AgentEventPayloadKeys.OutcomeStatus, out string status), Is.True);
            Assert.That(status, Is.EqualTo(PlanExecutionStatus.Succeeded.ToString()));
        }

        [UnityTest]
        public IEnumerator PlayerAreaMembership_TransitionsFromNoneToEntranceToCounter()
        {
            AreaMembershipService membership = Object.FindAnyObjectByType<AreaMembershipService>();
            GameObject entranceObject = GameObject.Find("CafeDemo/EntranceArea");
            GameObject counterObject = GameObject.Find("CafeDemo/CounterArea");
            Assert.That(membership, Is.Not.Null);
            Assert.That(entranceObject, Is.Not.Null);
            Assert.That(counterObject, Is.Not.Null);

            WorldEntity playerEntity = player.GetComponent<WorldEntity>();
            WorldEntity entrance = entranceObject.GetComponent<WorldEntity>();
            WorldEntity counter = counterObject.GetComponent<WorldEntity>();
            AreaVolume entranceVolume = entranceObject.GetComponent<AreaVolume>();
            AreaVolume counterVolume = counterObject.GetComponent<AreaVolume>();
            AgentEventRouter router = Object.FindAnyObjectByType<AgentEventRouter>();
            AreaMembershipAgentEventBridge areaBridge = Object.FindAnyObjectByType<AreaMembershipAgentEventBridge>();
            AgentEventReceiver[] eventReceivers = Object.FindObjectsByType<AgentEventReceiver>(
                FindObjectsInactive.Exclude);
            AgentEventReceiver merchantReceiver = eventReceivers.Single(value => value.EntityId == "npc_cafe_01");
            AgentEventReceiver regularReceiver = eventReceivers.Single(
                value => value.EntityId == "npc_cafe_regular_01");
            AgentEventReceiver backgroundReceiver = eventReceivers.Single(
                value => value.EntityId == "npc_cafe_background_01");
            CurrentBehaviorMode merchantMode = merchantReceiver.CurrentBehaviorMode;
            WakeCognitiveTriggerBridge merchantTriggerBridge =
                merchantReceiver.GetComponent<WakeCognitiveTriggerBridge>();
            AgentOrchestrator merchantOrchestrator = merchantReceiver.GetComponent<AgentOrchestrator>();
            CognitiveContextComposer merchantComposer = merchantReceiver.GetComponent<CognitiveContextComposer>();
            CognitiveRunDriver merchantDriver = merchantReceiver.GetComponent<CognitiveRunDriver>();
            BackendWebSocketClient backendClient = Object.FindAnyObjectByType<BackendWebSocketClient>();
            Assert.That(entrance.EntityId, Is.EqualTo("area_cafe_entrance"));
            Assert.That(entrance.DefinitionId, Is.EqualTo("cafe.area.entrance"));
            Assert.That(counter.EntityId, Is.EqualTo("area_cafe_counter"));
            Assert.That(counter.DefinitionId, Is.EqualTo("cafe.area.counter"));
            Assert.That(entranceVolume.TryValidateConfiguration(out string entranceError), Is.True, entranceError);
            Assert.That(counterVolume.TryValidateConfiguration(out string counterError), Is.True, counterError);
            Assert.That(router, Is.Not.Null);
            Assert.That(router.TryValidateConfiguration(out string routerError), Is.True, routerError);
            Assert.That(areaBridge, Is.Not.Null);
            Assert.That(merchantReceiver, Is.Not.Null);
            Assert.That(merchantReceiver.EntityId, Is.EqualTo("npc_cafe_01"));
            Assert.That(merchantReceiver.InterestProfile, Is.Not.Null);
            Assert.That(merchantMode, Is.Not.Null);
            Assert.That(merchantMode.CurrentModeId, Is.EqualTo(BehaviorModeIds.Working));
            Assert.That(merchantReceiver.ModeInterestOverrides, Is.Not.Null);
            Assert.That(merchantTriggerBridge, Is.Not.Null);
            CognitiveTrigger initialTrigger = merchantTriggerBridge.LastTrigger;
            CognitiveRun initialRun = merchantOrchestrator.LastRun;
            CognitiveRequest initialRequest = merchantComposer.LastRequest;
            Assert.That(merchantOrchestrator, Is.Not.Null);
            Assert.That(merchantComposer, Is.Not.Null);
            Assert.That(merchantDriver, Is.Not.Null);
            Assert.That(backendClient, Is.Not.Null);
            Assert.That(regularReceiver.InterestProfile, Is.Not.Null);
            Assert.That(backgroundReceiver.InterestProfile, Is.Not.Null);
            Assert.That(membership.TryGetPrimaryArea(playerEntity.EntityId, out _), Is.False);
            Assert.That(GameObject.Find("CafeDemo/BarArea").GetComponent<WorldEntity>(), Is.Null);
            Assert.That(GameObject.Find("CafeDemo/CustomerArea").GetComponent<WorldEntity>(), Is.Null);

            float connectionDeadline = Time.realtimeSinceStartup + 5f;
            while (!backendClient.IsConnected && Time.realtimeSinceStartup < connectionDeadline) yield return null;
            Assert.That(backendClient.IsConnected, Is.True, "Cafe mock backend must be connected for E5B transport smoke.");

            Vector3 entrancePosition = entranceObject.transform.position;
            Place(new Vector3(entrancePosition.x, 0.02f, entrancePosition.z));
            yield return Wait(0.1f);
            Assert.That(membership.TryGetPrimaryArea(playerEntity.EntityId, out WorldEntity currentArea), Is.True);
            Assert.That(currentArea, Is.SameAs(entrance));

            Vector3 counterPosition = counterObject.transform.position;
            Place(new Vector3(counterPosition.x, 0.02f, counterPosition.z));
            yield return Wait(0.1f);
            Assert.That(membership.TryGetPrimaryArea(playerEntity.EntityId, out currentArea), Is.True);
            Assert.That(currentArea, Is.SameAs(counter));
            Assert.That(merchantReceiver.LastEvent, Is.Not.Null);
            Assert.That(merchantReceiver.LastEvent.EventType, Is.EqualTo(AgentEventTypes.PlayerEnteredArea));
            Assert.That(merchantReceiver.LastEvent.SourceEntityId, Is.EqualTo("player_01"));
            Assert.That(merchantReceiver.LastEvent.AreaEntityId, Is.EqualTo("area_cafe_counter"));
            Assert.That(merchantReceiver.LastInterestDecision, Is.EqualTo(EventInterestDecision.Record));
            Assert.That(merchantTriggerBridge.LastTrigger, Is.SameAs(initialTrigger));
            Assert.That(merchantOrchestrator.LastRun, Is.SameAs(initialRun));
            Assert.That(merchantComposer.LastRequest, Is.SameAs(initialRequest));
            Assert.That(regularReceiver.LastEvent, Is.SameAs(merchantReceiver.LastEvent));
            Assert.That(regularReceiver.LastInterestDecision, Is.EqualTo(EventInterestDecision.Record));
            Assert.That(backgroundReceiver.LastEvent, Is.SameAs(merchantReceiver.LastEvent));
            Assert.That(backgroundReceiver.LastInterestDecision, Is.EqualTo(EventInterestDecision.Ignore));

            Assert.That(merchantMode.TrySetMode(BehaviorModeIds.OffDuty), Is.True);
            AgentEvent offDutyCounterEvent = new("cafe-off-duty-counter",
                AgentEventTypes.PlayerEnteredArea, "player_01", string.Empty,
                "area_cafe_counter", null, Time.realtimeSinceStartupAsDouble);
            Assert.That(router.Route(offDutyCounterEvent), Is.EqualTo(3));
            Assert.That(merchantReceiver.LastInterestDecision, Is.EqualTo(EventInterestDecision.Record));
            Assert.That(merchantTriggerBridge.LastTrigger, Is.SameAs(initialTrigger));
            Assert.That(merchantOrchestrator.LastRun, Is.SameAs(initialRun));
            Assert.That(merchantComposer.LastRequest, Is.SameAs(initialRequest));
            Assert.That(regularReceiver.LastInterestDecision, Is.EqualTo(EventInterestDecision.Record));
            Assert.That(backgroundReceiver.LastInterestDecision, Is.EqualTo(EventInterestDecision.Ignore));
        }

        [UnityTest]
        public IEnumerator CafeDialogueSubmission_UsesOneCognitiveRequestAndCompletesVisibleReply()
        {
            BackendWebSocketClient backend = Object.FindAnyObjectByType<BackendWebSocketClient>();
            DialogueController controller = Object.FindAnyObjectByType<DialogueController>();
            TextUtteranceInput input = Object.FindAnyObjectByType<TextUtteranceInput>();
            DialogueParticipant participant = DialogueHandler("npc_cafe_01").GetComponent<DialogueParticipant>();
            AgentEventReceiver[] eventReceivers = Object.FindObjectsByType<AgentEventReceiver>(
                FindObjectsInactive.Exclude);
            AgentEventReceiver merchantReceiver = eventReceivers.Single(value => value.EntityId == "npc_cafe_01");
            AgentOrchestrator orchestrator = merchantReceiver.GetComponent<AgentOrchestrator>();
            CognitiveContextComposer composer = merchantReceiver.GetComponent<CognitiveContextComposer>();
            DialogueBubbleManager bubbles = Object.FindAnyObjectByType<DialogueBubbleManager>();
            string utterance = "你好,今天有什么推荐?";

            float connectionDeadline = Time.realtimeSinceStartup + 5f;
            while (!backend.IsConnected && Time.realtimeSinceStartup < connectionDeadline) yield return null;
            Assert.That(backend.IsConnected, Is.True);
            Place(new Vector3(-0.8f, 0.02f, 1.25f));
            yield return Wait(0.1f);
            float areaRunDeadline = Time.realtimeSinceStartup + 5f;
            while (orchestrator.HasActiveRun && Time.realtimeSinceStartup < areaRunDeadline) yield return null;
            Assert.That(orchestrator.HasActiveRun, Is.False, "Counter-area setup run must finish before dialogue.");
            Assert.That(controller.SelectTarget(participant), Is.True);
            int cognitiveBefore = backend.CognitiveRequestSendCount;

            Assert.That(input.SubmitText(utterance), Is.True);
            Assert.That(merchantReceiver.LastEvent, Is.Not.Null);
            Assert.That(merchantReceiver.LastEvent.EventType, Is.EqualTo(AgentEventTypes.PlayerUtterance));
            Assert.That(merchantReceiver.LastEvent.SourceEntityId, Is.EqualTo("player_01"));
            Assert.That(merchantReceiver.LastEvent.TargetEntityId, Is.EqualTo("npc_cafe_01"));
            Assert.That(merchantReceiver.LastEvent.TryGetPayload(AgentEventPayloadKeys.Text, out string text), Is.True);
            Assert.That(text, Is.EqualTo(utterance));
            Assert.That(merchantReceiver.LastInterestDecision, Is.EqualTo(EventInterestDecision.Wake));
            Assert.That(composer.LastRequest, Is.Not.Null);
            Assert.That(composer.LastRequest.context.current_event.payload.Single(value =>
                value.key == AgentEventPayloadKeys.Text).value, Is.EqualTo(utterance));

            float responseDeadline = Time.realtimeSinceStartup + 5f;
            while ((orchestrator.LastRun == null || !orchestrator.LastRun.IsTerminal) &&
                   Time.realtimeSinceStartup < responseDeadline) yield return null;

            Assert.That(backend.CognitiveRequestSendCount - cognitiveBefore, Is.EqualTo(1));
            Assert.That(orchestrator.LastRun.State, Is.EqualTo(CognitiveRunState.Completed));
            Assert.That(bubbles.GetMessages("npc_cafe_01"), Is.Not.Empty);
            Assert.That(bubbles.GetMessages("npc_cafe_01").Last().Text, Is.Not.Empty);
        }

        [UnityTest]
        public IEnumerator RegularCustomer_UsesIndependentCognitiveRunWithoutActionAuthority()
        {
            BackendWebSocketClient backend = Object.FindAnyObjectByType<BackendWebSocketClient>();
            DialogueController controller = Object.FindAnyObjectByType<DialogueController>();
            TextUtteranceInput input = Object.FindAnyObjectByType<TextUtteranceInput>();
            DialogueBubbleManager bubbles = Object.FindAnyObjectByType<DialogueBubbleManager>();
            WorldStateService worldState = Object.FindAnyObjectByType<WorldStateService>();
            EntityRegistry registry = Object.FindAnyObjectByType<EntityRegistry>();
            WorldEntity[] entities = Object.FindObjectsByType<WorldEntity>(FindObjectsInactive.Exclude);
            WorldEntity customer = entities.Single(value => value.EntityId == "npc_cafe_regular_01");
            WorldEntity merchant = entities.Single(value => value.EntityId == "npc_cafe_01");
            DialogueParticipant participant = customer.GetComponent<DialogueParticipant>();
            AgentEventReceiver receiver = customer.GetComponent<AgentEventReceiver>();
            AgentOrchestrator orchestrator = customer.GetComponent<AgentOrchestrator>();
            CognitiveContextComposer composer = customer.GetComponent<CognitiveContextComposer>();
            CognitiveRunDriver driver = customer.GetComponent<CognitiveRunDriver>();
            AgentActionCapabilityProfile capabilities = customer.GetComponent<AgentActionCapabilityProfile>();
            DialogueBubbleAnchor anchor = customer.GetComponent<DialogueBubbleAnchor>();

            Assert.That(participant, Is.Not.Null);
            Assert.That(receiver, Is.Not.Null);
            Assert.That(orchestrator, Is.Not.Null);
            Assert.That(composer, Is.Not.Null);
            Assert.That(driver, Is.Not.Null);
            Assert.That(customer.GetComponent<GoalPlanRunner>(), Is.Null);
            Assert.That(customer.GetComponent<CognitiveActionHandoff>(), Is.Null);
            Assert.That(capabilities.AllowedActionIds, Is.Empty);
            Assert.That(composer.CharacterDefinition.CharacterId, Is.EqualTo("cafe.regular_customer"));
            Assert.That(merchant.GetComponent<CognitiveContextComposer>().CharacterDefinition.CharacterId,
                Is.EqualTo("cafe.merchant"));
            Assert.That(anchor.SpeakerEntityId, Is.EqualTo(customer.EntityId));
            Assert.That(anchor.AnchorPoint, Is.Not.SameAs(customer.transform));

            float connectionDeadline = Time.realtimeSinceStartup + 5f;
            while (!backend.IsConnected && Time.realtimeSinceStartup < connectionDeadline) yield return null;
            Assert.That(backend.IsConnected, Is.True);
            Place(customer.transform.position + Vector3.right * .5f);
            yield return Wait(.1f);
            Assert.That(controller.SelectTarget(participant), Is.True);

            int merchantBubbleCount = bubbles.GetMessages(merchant.EntityId).Count;
            Assert.That(input.SubmitText("你好，今天过得怎么样？"), Is.True);
            CognitiveRun directRun = orchestrator.CurrentRun;
            Assert.That(directRun, Is.Not.Null);
            float directDeadline = Time.realtimeSinceStartup + 5f;
            while (!directRun.IsTerminal && Time.realtimeSinceStartup < directDeadline) yield return null;

            Assert.That(directRun.State, Is.EqualTo(CognitiveRunState.Completed),
                $"send={driver.LastSendSucceeded}; dispatches={backend.CognitiveResponseDispatchCount}; " +
                $"ingress={backend.LastCognitiveIngressStatus}; request={composer.LastRequest?.request_id}; " +
                $"json={JsonUtility.ToJson(composer.LastRequest)}");
            Assert.That(orchestrator.LastRun, Is.SameAs(directRun));
            Assert.That(receiver.LastEvent.TargetEntityId, Is.EqualTo(customer.EntityId));
            Assert.That(composer.LastRequest.run.agent_entity_id, Is.EqualTo(customer.EntityId));
            Assert.That(composer.LastRequest.context.agent_entity_id, Is.EqualTo(customer.EntityId));
            Assert.That(composer.LastRequest.context.character.character_id,
                Is.EqualTo("cafe.regular_customer"));
            Assert.That(composer.LastRequest.context.capabilities.available_actions, Is.Empty);
            Assert.That(bubbles.GetMessages(customer.EntityId), Is.Not.Empty);
            Assert.That(bubbles.GetMessages(merchant.EntityId).Count, Is.EqualTo(merchantBubbleCount));

            Assert.That(registry.TryGetEntity("cup_01", out WorldEntity cup), Is.True);
            HeldItemState held = cup.GetComponent<HeldItemState>();
            string holderBefore = held.HolderEntityId;
            int revisionBefore = worldState.WorldRevision;
            Assert.That(input.SubmitText("把这个杯子拿给我"), Is.True);
            CognitiveRun actionLikeRun = orchestrator.CurrentRun;
            Assert.That(actionLikeRun, Is.Not.Null);
            Assert.That(actionLikeRun, Is.Not.SameAs(directRun));
            float actionDeadline = Time.realtimeSinceStartup + 5f;
            while (!actionLikeRun.IsTerminal && Time.realtimeSinceStartup < actionDeadline) yield return null;

            Assert.That(actionLikeRun.State, Is.EqualTo(CognitiveRunState.Completed));
            Assert.That(driver.LastValidatedGoalProposal, Is.Null);
            Assert.That(held.HolderEntityId, Is.EqualTo(holderBefore));
            Assert.That(worldState.WorldRevision, Is.EqualTo(revisionBefore));
            Assert.That(bubbles.GetMessages(customer.EntityId).Count, Is.GreaterThanOrEqualTo(2));
        }

        [UnityTest]
        public IEnumerator CafeDialogueSubmission_WhenRunBusy_DoesNotRouteOrFallbackAndPreservesText()
        {
            BackendWebSocketClient backend = Object.FindAnyObjectByType<BackendWebSocketClient>();
            DialogueController controller = Object.FindAnyObjectByType<DialogueController>();
            TextUtteranceInput input = Object.FindAnyObjectByType<TextUtteranceInput>();
            CognitiveDialogueSubmissionHandler handler = DialogueHandler("npc_cafe_01");
            DialogueParticipant participant = handler.GetComponent<DialogueParticipant>();
            AgentOrchestrator orchestrator = handler.GetComponent<AgentOrchestrator>();
            CognitiveContextComposer composer = handler.GetComponent<CognitiveContextComposer>();
            AgentEventReceiver receiver = handler.GetComponent<AgentEventReceiver>();

            float connectionDeadline = Time.realtimeSinceStartup + 5f;
            while (!backend.IsConnected && Time.realtimeSinceStartup < connectionDeadline) yield return null;
            Assert.That(backend.IsConnected, Is.True);
            Place(new Vector3(-0.8f, 0.02f, 1.25f));
            yield return Wait(0.1f);
            float areaRunDeadline = Time.realtimeSinceStartup + 5f;
            while (orchestrator.HasActiveRun && Time.realtimeSinceStartup < areaRunDeadline) yield return null;
            Assert.That(orchestrator.HasActiveRun, Is.False, "Counter-area setup run must finish before busy fixture.");
            Assert.That(controller.SelectTarget(participant), Is.True);
            composer.enabled = false;
            Assert.That(orchestrator.TryStartRun(new CognitiveTrigger("busy-trigger", CognitiveTriggerTypes.Event,
                "npc_cafe_01", string.Empty, 1d, "test.busy"), out CognitiveRun busyRun), Is.True);
            AgentEvent previousEvent = receiver.LastEvent;
            int cognitiveBefore = backend.CognitiveRequestSendCount;
            Assert.That(handler.LastAcceptedTurn, Is.Null);

            Assert.That(input.SubmitText("请稍等后再回答"), Is.True);
            yield return null;

            Assert.That(orchestrator.CurrentRun, Is.SameAs(busyRun));
            Assert.That(receiver.LastEvent, Is.SameAs(previousEvent));
            Assert.That(backend.CognitiveRequestSendCount, Is.EqualTo(cognitiveBefore));
            Assert.That(handler.LastAcceptedTurn, Is.Null,
                "A busy/rejected submission must not cross the accepted Experience boundary.");
            Assert.That(input.CurrentText, Is.EqualTo("请稍等后再回答"));
            Assert.That(orchestrator.CancelCurrentRun(), Is.True);
        }

        [UnityTest]
        public IEnumerator Wasd_IsCameraRelative_AndGravityGroundsPlayer()
        {
            var origin=new Vector3(2.9f,.03f,1.2f);
            yield return Aim(0,30);
            var keys=new[]{Key.W,Key.S,Key.A,Key.D};var directions=new[]{Vector3.forward,Vector3.back,Vector3.left,Vector3.right};
            for(int i=0;i<keys.Length;i++)
            {
                Place(origin);yield return null;var before=player.transform.position;
                yield return KeyFor(keys[i],.22f);
                Assert.That(Vector3.Dot(player.transform.position-before,directions[i]),Is.GreaterThan(.30f),keys[i].ToString());
            }
            yield return Aim(90,30);Place(origin);yield return null;var start=player.transform.position;
            yield return KeyFor(Key.W,.22f);
            Assert.That(player.transform.position.x-start.x,Is.GreaterThan(.30f),"W follows rotated camera");
            Assert.That(Mathf.Abs(player.transform.position.z-start.z),Is.LessThan(.1f));
            Place(new Vector3(2.9f,.65f,1.2f));yield return Wait(.65f);
            Assert.That(player.transform.position.y,Is.InRange(-.04f,.08f));Assert.That(player.Controller.isGrounded,Is.True);
        }

        [UnityTest]
        public IEnumerator Player_CannotCrossWallOrCounter_AndCanPassOpenDoor()
        {
            yield return Aim(0,30);Place(new Vector3(4.1f,.03f,2));
            yield return KeyFor(Key.D,1.1f);
            Assert.That(player.transform.position.x,Is.InRange(4.5f,4.73f),"East wall collision");
            Place(new Vector3(-1.2f,.03f,2.1f));yield return KeyFor(Key.W,1.1f);
            Assert.That(player.transform.position.z,Is.InRange(2.4f,2.58f),"Continuous counter collision");
            Place(new Vector3(-.6f,.03f,-6.8f));yield return KeyFor(Key.W,.85f);
            Assert.That(player.transform.position.z,Is.GreaterThan(-5.2f),"Pass through open entrance");
            Assert.That(player.transform.position.y,Is.InRange(-.04f,.08f));
        }

        [UnityTest]
        public IEnumerator Orbit_MouseLookZoomPitchLimits_CollisionAndRecovery()
        {
            // Reproduce the reported one-notch input at the actual indoor spawn.
            yield return MouseEvent(Vector2.zero);
            float spawnDistance=orbit.ActualDistance;
            yield return MouseEvent(Vector2.zero,1,false);
            Assert.That(orbit.ActualDistance,Is.LessThan(spawnDistance-.3f),"One normalized wheel notch visibly zooms in at spawn");
            float closeDistance=orbit.ActualDistance;
            yield return MouseEvent(Vector2.zero,-1,false);yield return Wait(.5f);
            Assert.That(orbit.ActualDistance,Is.GreaterThan(closeDistance+.3f),"Wheel out visibly recovers distance");
            Place(new Vector3(2.9f,.03f,1.2f));float originalYaw=orbit.Yaw;
            yield return MouseEvent(new Vector2(200,0),0,false);
            Assert.That(Mathf.DeltaAngle(originalYaw,orbit.Yaw),Is.EqualTo(36).Within(.2f),"Mouse look needs no RMB");
            var settings=Object.FindAnyObjectByType<CafeDemoDeveloperPanel>();
            SettingsEscape(settings,true);SettingsEscape(settings,false);float releasedYaw=orbit.Yaw;
            Assert.That(settings.IsOpen,Is.True,"Escape opens settings during gameplay");
            Assert.That(orbit.PointerCaptured,Is.False);
            yield return MouseEvent(new Vector2(200,0),1,false);
            Assert.That(orbit.Yaw,Is.EqualTo(releasedYaw).Within(.01f),"Settings cursor can operate UI without orbit");
            yield return MouseEvent(Vector2.zero);Assert.That(orbit.PointerCaptured,Is.False);
            SettingsEscape(settings,true);SettingsEscape(settings,false);
            Assert.That(settings.IsOpen,Is.False,"Escape closes settings");
            Assert.That(orbit.PointerCaptured,Is.True);
            yield return MouseEvent(new Vector2(500,0),0,false);
            Assert.That(Mathf.DeltaAngle(releasedYaw,orbit.Yaw),Is.EqualTo(90).Within(.2f));
            yield return MouseEvent(Vector2.zero,-10,false);
            yield return MouseEvent(new Vector2(0,-1000));Assert.That(orbit.Pitch,Is.EqualTo(55).Within(.01f));
            yield return MouseEvent(new Vector2(0,1000));Assert.That(orbit.Pitch,Is.EqualTo(-10).Within(.01f));
            yield return Aim(0,15);yield return Wait(1.1f);
            float clearDistance=orbit.ActualDistance;Assert.That(clearDistance,Is.GreaterThan(4.5f));
            Place(new Vector3(2.9f,.03f,-5.35f));yield return null;yield return null;
            float blocked=orbit.ActualDistance;Assert.That(blocked,Is.LessThan(.85f),"Camera pulls in before south wall");
            Assert.That(player.GetComponentsInChildren<Renderer>().All(r=>!r.enabled),Is.True,"Hide close capsule to preserve view");
            AssertCameraClear();
            Place(new Vector3(2.9f,.03f,1.2f));yield return null;yield return null;
            Assert.That(orbit.ActualDistance,Is.LessThan(clearDistance-.1f),"Recovery is gradual");
            yield return Wait(1.3f);Assert.That(orbit.ActualDistance,Is.GreaterThan(clearDistance-.15f));
            Assert.That(player.GetComponentsInChildren<Renderer>().All(r=>r.enabled),Is.True,"Restore player visual after camera recovery");
            for(int angle=0;angle<=360;angle+=30)
            {
                yield return Aim(angle,30);AssertCameraClear();
                Assert.That(Vector3.Dot(orbit.transform.forward,(orbit.Target.position-orbit.transform.position).normalized),Is.GreaterThan(.999f));
            }
        }
        private void AssertCameraClear()
        {
            var overlaps=Physics.OverlapSphere(orbit.transform.position,orbit.CollisionRadius-.02f,~0,QueryTriggerInteraction.Ignore);
            foreach(var c in overlaps)if(!c.transform.IsChildOf(player.transform))Assert.Fail("Camera penetrates "+c.name);
        }

        [UnityTest]
        public IEnumerator CharacterVisuals_IdleGroundingFacingAndCollisionOwnership()
        {
            IsolateManualVisualAndRouteChecks();
            yield return null;
            var npc=Object.FindAnyObjectByType<CafeDemoNavMeshTestAgent>();
            var actors=new[]{player.gameObject,npc.gameObject};
            foreach(var actor in actors)
            {
                var visual=actor.transform.Find("Visual");Assert.That(visual,Is.Not.Null);
                Assert.That(visual.GetComponentsInChildren<Collider>(true),Is.Empty,"Visual must not own movement collision");
                var animator=visual.GetComponentInChildren<Animator>();Assert.That(animator,Is.Not.Null);
                Assert.That(animator.avatar.isValid&&animator.isHuman,Is.True);
                Assert.That(animator.applyRootMotion,Is.False);
                Assert.That(animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Locomotion"),Is.True);
                Assert.That(animator.GetFloat("Speed"),Is.LessThan(.01f),"Current locomotion blend tree is idle at zero speed");
                float before=animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
                yield return Wait(.15f);
                Assert.That(animator.GetCurrentAnimatorStateInfo(0).normalizedTime,Is.GreaterThan(before));
                var facing=animator.GetBoneTransform(HumanBodyBones.LeftToes).position-animator.GetBoneTransform(HumanBodyBones.LeftFoot).position;
                Assert.That(Vector3.Dot(facing,actor.transform.forward),Is.GreaterThan(.04f),"Feet face along actor forward");
                for(int sample=0;sample<5;sample++)
                {
                    animator.Play("Base Layer.Locomotion",0,sample*.2f);animator.Update(0);
                    var bounds=CharacterBounds(actor);
                    Assert.That(bounds.min.y,Is.InRange(-.025f,.025f),"Feet align with actor ground through Idle");
                    Assert.That(bounds.size.y,Is.InRange(1.70f,1.82f),"Visual fits existing 1.8m body");
                    Assert.That(bounds.size.x,Is.LessThan(.66f),"Visual width fits collision body approximately");
                }
            }
            Assert.That(player.GetComponentsInChildren<CharacterController>().Length,Is.EqualTo(1));
            Assert.That(npc.GetComponentsInChildren<UnityEngine.AI.NavMeshAgent>().Length,Is.EqualTo(1));
            LogAssert.NoUnexpectedReceived();
        }

        private static Bounds CharacterBounds(GameObject actor)
        {
            var bounds=new Bounds();bool first=true;
            foreach(var renderer in actor.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                var mesh=new Mesh();renderer.BakeMesh(mesh,true);
                foreach(var vertex in mesh.vertices)
                {
                    var point=actor.transform.InverseTransformPoint(renderer.transform.TransformPoint(vertex));
                    if(first){bounds=new Bounds(point,Vector3.zero);first=false;}else bounds.Encapsulate(point);
                }
                Object.Destroy(mesh);
            }
            Assert.That(first,Is.False,"Expected character geometry");return bounds;
        }

        [UnityTest]
        public IEnumerator NavMesh_FloorOnly_AllPairsAndActualAgentRoutes()
        {
            IsolateManualVisualAndRouteChecks();
            Place(new Vector3(4.3f,.03f,2));
            var tester=Object.FindAnyObjectByType<CafeDemoNavMeshTestAgent>();var agent=tester.Agent;
            var points=tester.TestPoints;var filter=new NavMeshQueryFilter{agentTypeID=agent.agentTypeID,areaMask=agent.areaMask};
            var merchantEntity=tester.GetComponent<WorldEntity>();
            Assert.That(merchantEntity.EntityId,Is.EqualTo("npc_cafe_01"));
            Assert.That(merchantEntity.DefinitionId,Is.EqualTo("character.npc.default"));
            Assert.That(Vector3.Distance(tester.transform.position,new Vector3(-.8f,.03f,4.35f)),Is.LessThan(.02f));
            Assert.That(Vector3.Dot(tester.transform.forward,Vector3.back),Is.GreaterThan(.999f));
            Assert.That(agent.isOnNavMesh,Is.True);
            var triangulation=NavMesh.CalculateTriangulation();Assert.That(triangulation.indices.Length,Is.GreaterThan(0));
            Assert.That(triangulation.vertices.All(v=>v.y>-.05f&&v.y<.10f),Is.True,"No table/counter/sofa/seat-top NavMesh");
            foreach(var a in points)foreach(var b in points)
            {
                if(a==b)continue;var path=new NavMeshPath();
                Assert.That(NavMesh.CalculatePath(a.position,b.position,filter,path),Is.True);
                Assert.That(path.status,Is.EqualTo(NavMeshPathStatus.PathComplete),a.name+" -> "+b.name);
            }
            var entryOutside=new Vector3(-.6f,.03f,-6.8f);var doorwayPath=new NavMeshPath();
            Assert.That(NavMesh.CalculatePath(entryOutside,points[0].position,filter,doorwayPath),Is.True);
            Assert.That(doorwayPath.status,Is.EqualTo(NavMeshPathStatus.PathComplete));
            Assert.That(agent.Warp(entryOutside),Is.True);
            int[] route={0,1,4,3,0,2,0};
            foreach(int index in route)
            {
                Assert.That(tester.MoveTo(points[index]),Is.True);float deadline=Time.time+20;
                do
                {
                    yield return null;
                    Assert.That(agent.isOnNavMesh,Is.True);
                    Assert.That(agent.transform.position.y,Is.InRange(-.05f,.12f));
                    AssertNoFurniturePenetration(agent);
                    Assert.That(Time.time,Is.LessThan(deadline),"Timed out walking to "+points[index].name);
                }while(agent.pathPending||agent.remainingDistance>agent.stoppingDistance+.06f||agent.velocity.sqrMagnitude>.03f);
                Assert.That(Vector3.Distance(agent.transform.position,points[index].position),Is.LessThan(.25f));
            }
            var unreachable=new GameObject("UnreachableTestTarget");unreachable.transform.position=new Vector3(50,0,50);
            Assert.That(tester.MoveTo(unreachable.transform),Is.False);Object.Destroy(unreachable);
            LogAssert.NoUnexpectedReceived();
        }
        private static void IsolateManualVisualAndRouteChecks()
        {
            // These checks own the idle pose / manual NavMesh path, not the autonomous routine.
            foreach (var driver in Object.FindObjectsByType<CognitiveRunDriver>(FindObjectsSortMode.None))
                driver.enabled = false;
            foreach (var routine in Object.FindObjectsByType<EmbodiedNPC.Routines.NpcDefaultRoutineController>(FindObjectsSortMode.None))
                routine.enabled = false;
            foreach (var agent in Object.FindObjectsByType<NavMeshAgent>(FindObjectsSortMode.None))
                if (agent.isOnNavMesh) agent.ResetPath();
        }

        private void AssertNoFurniturePenetration(NavMeshAgent agent)
        {
            var body=agent.GetComponent<CapsuleCollider>();
            var nearby=Physics.OverlapCapsule(agent.transform.position+Vector3.up*.33f,agent.transform.position+Vector3.up*1.47f,.29f);
            foreach(var collider in nearby)
            {
                if(collider.isTrigger||collider.transform.IsChildOf(agent.transform)||collider.transform.IsChildOf(player.transform))continue;
                Vector3 direction;float depth;
                if(Physics.ComputePenetration(body,body.transform.position,body.transform.rotation,collider,collider.transform.position,collider.transform.rotation,out direction,out depth))
                    Assert.That(depth,Is.LessThan(.025f),"Agent clips "+collider.name);
            }
        }

        private static CognitiveDialogueSubmissionHandler DialogueHandler(string entityId) =>
            Object.FindObjectsByType<CognitiveDialogueSubmissionHandler>(FindObjectsInactive.Exclude)
                .Single(value => value.GetComponent<WorldEntity>().EntityId == entityId);
    }
}
