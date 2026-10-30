using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Presentation;
using EmbodiedNPC.Events;
using EmbodiedNPC.Interaction;
using EmbodiedNPC.Memory;
using EmbodiedNPC.Actions;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Planning.MiniGoap;
using EmbodiedNPC.Settings;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    // Real Cafe navigation/world, injected decisions, no Backend connection or model call.
    public sealed class CafeP1aLifecycleTests
    {
        private CognitiveDialogueSubmissionHandler merchant;
        private CognitiveRunDriver driver;
        private AgentOrchestrator orchestrator;
        private GoalPlanRunner runner;
        private DialogueController dialogue;
        private WorldEntity player, cup;
        private WorldStateService world;
        private readonly List<GoalOutcome> outcomes = new();
        private readonly List<PresentedCognitiveUtterance> utterances = new();
        private readonly List<ExperienceDraft> dialogueDrafts = new();

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            PrepareScene();
            yield return new EnterPlayMode();
            BindScene();
        }

        private void PrepareScene()
        {
            // A fresh checkout can import VRM before its package shaders are ready.
            // Reimport only failed model dependencies, preserving their original bytes/GUIDs.
            foreach (var path in new[] { "Assets/CafeEnvironmentCompatibilityTest/GirlA.vrm",
                "Assets/VRMCompatibilityTest/Xingmang.vrm", "Assets/CafeDemo/V2/Characters/薇薇.vrm",
                "Assets/CafeDemo/V2/Characters/桜田 史利矢.vrm", "Assets/CafeDemo/V2/Characters/Ao.vrm" })
            {
                if (AssetDatabase.LoadAssetAtPath<GameObject>(path) == null)
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(path), Is.Not.Null, path);
            }
            EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
            // Production startup creates this scene token after config/transport admission.
            // Offline acceptance needs the same scope carrier, with all Task/network behavior disabled.
            var panel = Object.FindAnyObjectByType<CafeDemoDeveloperPanel>();
            (panel.GetComponent<TaskRuntimeBridge>() ?? panel.gameObject.AddComponent<TaskRuntimeBridge>()).enabled = false;
            foreach (var backend in Object.FindObjectsByType<BackendWebSocketClient>(FindObjectsInactive.Include))
            {
                var fields = new SerializedObject(backend);
                fields.FindProperty("connectOnStart").boolValue = false;
                fields.ApplyModifiedPropertiesWithoutUndo();
                backend.enabled = false;
            }
            // Keep startup UI and autonomous requests from contacting any running user service.
            foreach (var behaviour in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include))
                if (behaviour is FirstRunModelSetup || behaviour is ModelConfigClient ||
                    behaviour is DialogueHistoryPanel || behaviour is RuntimeTraceRecorder || behaviour is CognitiveRunDriver ||
                    behaviour is TaskRuntimeBridge || behaviour is TaskConsiderationSource ||
                    behaviour is DialogueContinuationScheduler ||
                    behaviour is EmbodiedNPC.Routines.ServiceZoneDwellCognitionSource ||
                    behaviour is EmbodiedNPC.Routines.CounterCleaningCognitionSource)
                    behaviour.enabled = false;
        }

        private void BindScene()
        {
            var entities = Object.FindObjectsByType<WorldEntity>();
            merchant = entities.Single(e => e.EntityId == "npc_cafe_01").GetComponent<CognitiveDialogueSubmissionHandler>();
            player = entities.Single(e => e.EntityId == "player_01");
            cup = entities.Single(e => e.EntityId == "cup_01");
            driver = merchant.GetComponent<CognitiveRunDriver>();
            orchestrator = merchant.GetComponent<AgentOrchestrator>();
            runner = merchant.GetComponent<GoalPlanRunner>();
            world = Object.FindAnyObjectByType<WorldStateService>();
            dialogue = Object.FindAnyObjectByType<DialogueController>();
            Object.FindAnyObjectByType<CafeDemoDeveloperPanel>().Close();
            dialogue.SetSetupReady(true);
            MovePlayer(merchant.transform.position + merchant.transform.right * 1.2f);
            Assert.That(dialogue.SelectTarget(merchant.GetComponent<DialogueParticipant>()), Is.True, dialogue.LastEligibility.FailureCode.ToString());
            merchant.transform.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(
                cup.GetComponent<PerceivableEntity>().VisualAnchor.position - merchant.transform.position, Vector3.up));
            Physics.SyncTransforms();
            merchant.GetComponent<VisionSensor>().ScanNow();
            Assert.That(merchant.GetComponent<VisionSensor>().CurrentVisibleEntityIds, Does.Contain("cup_01"));
            outcomes.Clear(); utterances.Clear();
            dialogueDrafts.Clear();
            runner.GoalOutcomePublished += outcomes.Add;
            driver.UtterancePresented += utterances.Add;
            var adapter = new DialogueExperienceAdapter(dialogueDrafts.Add, Object.FindAnyObjectByType<EntityRegistry>());
            driver.UtterancePresented += adapter.Handle;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (Application.isPlaying) yield return new ExitPlayMode();
            SessionState.EraseString("P1b.Reload.Request");
            SessionState.EraseString("P1b.Reload.State");
            // Discard only this fixture's unsaved setup; never save the formal scene.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [UnityTest]
        public IEnumerator AcceptedGoal_AllowsChatAndRejectsSecondAction_OutcomeCannotEndNewRun()
        {
            var original = BeginUtterance("把这个杯子拿给我");
            var originalRun = orchestrator.CurrentRun;
            int revision = world.WorldRevision;
            Assert.That(driver.TryConsumeResponse(Response(original, true)), Is.True);
            Assert.That(originalRun.State, Is.EqualTo(CognitiveRunState.Completed));
            Assert.That(orchestrator.HasActiveRun, Is.False);
            Assert.That(driver.HasPendingActionOutcome && runner.HasActiveTask, Is.True);
            string proposal = runner.ActiveProposalId;

            var chat = BeginUtterance("你有什么兴趣？");
            Assert.That(driver.TryConsumeResponse(Response(chat, false)), Is.True);
            Assert.That(utterances.Last().RequestId, Is.EqualTo(chat.request_id));
            Assert.That(runner.ActiveProposalId, Is.EqualTo(proposal));

            var secondAction = BeginUtterance("再拿一次杯子");
            Assert.That(driver.TryConsumeResponse(Response(secondAction, true)), Is.False);
            Assert.That(driver.LastActionHandoffResult, Is.EqualTo("action_busy"));
            Assert.That(runner.ActiveProposalId, Is.EqualTo(proposal));
            Assert.That(driver.HasPendingActionOutcome, Is.True);

            var pendingChat = BeginUtterance("再聊一句");
            var chatRun = orchestrator.CurrentRun;
            float until = Time.realtimeSinceStartup + 32f;
            while (outcomes.Count == 0 && Time.realtimeSinceStartup < until) yield return null;
            Assert.That(outcomes, Has.Count.EqualTo(1));
            Assert.That(outcomes[0].ProposalId, Is.EqualTo(proposal));
            Assert.That(outcomes[0].TraceId, Is.EqualTo(original.trace_id));
            Assert.That(outcomes[0].Status, Is.EqualTo(PlanExecutionStatus.Succeeded));
            Assert.That(cup.GetComponent<HeldItemState>().HolderEntityId, Is.EqualTo(player.EntityId));
            Assert.That(world.WorldRevision, Is.EqualTo(revision + 2));
            Assert.That(driver.HasPendingActionOutcome, Is.False);
            Assert.That(orchestrator.CurrentRun, Is.SameAs(chatRun));
            Assert.That(chatRun.State, Is.EqualTo(CognitiveRunState.Running));
            Assert.That(driver.PendingRequest, Is.SameAs(pendingChat));
            Assert.That(driver.TryConsumeResponse(Response(pendingChat, false)), Is.False,
                "Only consuming the now-stale reply may settle its chat Run; the old Outcome above must not.");
            Assert.That(originalRun.State, Is.EqualTo(CognitiveRunState.Completed));
            Assert.That(chatRun.State, Is.EqualTo(CognitiveRunState.Failed));
            var freshChat = BeginUtterance("杯子现在怎么样了？");
            Assert.That(freshChat.context.runtime_state.status, Is.EqualTo("SUCCEEDED"));
            Assert.That(driver.TryConsumeResponse(Response(freshChat, false)), Is.True);
        }

        [UnityTest]
        public IEnumerator SessionChanges_KeepAcceptedGoal_ExplicitCancelSettlesOnlyExecution()
        {
            merchant.GetComponent<NavMeshAgent>().speed = 0f; // Hold the real accepted goal during UI transitions.
            var original = BeginUtterance("把这个杯子拿给我");
            var originalRun = orchestrator.CurrentRun;
            Assert.That(driver.TryConsumeResponse(Response(original, true)), Is.True);
            string proposal = runner.ActiveProposalId;
            var late = BeginUtterance("这条回复会迟到");
            int shown = utterances.Count;
            dialogue.HideDialoguePresentation();
            Assert.That(runner.HasActiveTask && driver.HasPendingActionOutcome, Is.True);
            dialogue.ClearSession();
            Assert.That(runner.HasActiveTask && driver.HasPendingActionOutcome, Is.True);
            var other = Object.FindObjectsByType<DialogueParticipant>().First(p => p.Entity.EntityId != merchant.GetComponent<WorldEntity>().EntityId);
            MovePlayer(other.transform.position + Vector3.forward);
            Assert.That(dialogue.SelectTarget(other), Is.True);
            Assert.That(driver.TryConsumeResponse(Response(late, false)), Is.False);
            Assert.That(utterances, Has.Count.EqualTo(shown));
            Assert.That(runner.ActiveProposalId, Is.EqualTo(proposal));
            Assert.That(outcomes, Is.Empty);
            runner.CancelCurrentPlan();
            Assert.That(outcomes, Has.Count.EqualTo(1));
            Assert.That(outcomes[0].Status, Is.EqualTo(PlanExecutionStatus.Cancelled));
            Assert.That(outcomes[0].ProposalId, Is.EqualTo(proposal));
            Assert.That(driver.HasPendingActionOutcome, Is.False);
            Assert.That(originalRun.State, Is.EqualTo(CognitiveRunState.Completed));
            Assert.That(dialogue.SelectedTarget, Is.SameAs(other));
            Assert.That(utterances, Has.Count.EqualTo(shown));
            yield return null;
        }

        [UnityTest]
        public IEnumerator Coffee12Seconds_ChatAcceptedBeforeOutcome_ClosesWithoutCancellation() => RunTimedCoffee(false);

        [UnityTest]
        public IEnumerator Coffee12Seconds_ExplicitCancelDuringPreparation_DoesNotSettleChat() => RunTimedCoffee(true);

        [UnityTest]
        public IEnumerator P1bCoffee12Seconds_StationFailureIsNotSuccess() => RunTimedCoffee(false, true);

        private IEnumerator RunTimedCoffee(bool cancel, bool fail = false)
        {
            const float seconds = 12f;
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            // Replace only this scene instance's idle runner. No scene/config/default is saved or changed.
            var executor = typeof(GoalPlanRunner).GetField("executor", fields).GetValue(runner);
            var set = typeof(PlanExecutor).GetField("runnerSet", fields).GetValue(executor);
            var runners = (Dictionary<PlannerActionType, IPlanActionRunner>)typeof(PlanActionRunnerSet)
                .GetField("runners", fields).GetValue(set);
            Assert.That(typeof(PrepareDrinkActionRunner).GetField("preparationSeconds", fields)
                .GetValue(runners[PlannerActionType.PrepareDrink]), Is.EqualTo(PrepareDrinkActionRunner.PreparationSeconds));
            runners[PlannerActionType.PrepareDrink] = new PrepareDrinkActionRunner(seconds);

            var original = BeginUtterance("请做一杯拿铁");
            var originalRun = orchestrator.CurrentRun;
            var response = Response(original, true);
            response.decision.utterance = "我去做一杯拿铁。";
            response.decision.action_proposal = new CognitiveActionProposal { action_id = "prepare_drink",
                bindings = new Dictionary<string, string> { ["drink_type"] = "latte" } };
            response.validated_goal = new GoalProposal { type = "prepare_drink", drink_type = "latte" };
            int revision = world.WorldRevision, drinks = world.GetDrinkCount(player.EntityId, "latte");
            Assert.That(driver.TryConsumeResponse(response), Is.True, driver.LastActionHandoffResult);
            string proposal = runner.ActiveProposalId;
            Assert.That(originalRun.State, Is.EqualTo(CognitiveRunState.Completed));
            var timing = new CoffeeTiming { scenario = fail ? "fail" : cancel ? "cancel" : "complete", seconds = seconds,
                proposal = proposal, trace = original.trace_id, originalRun = originalRun.RunId,
                accepted = Time.realtimeSinceStartupAsDouble };
            double outcomeAt = 0;
            runner.GoalOutcomePublished += _ => outcomeAt = Time.realtimeSinceStartupAsDouble;
            float until = Time.realtimeSinceStartup + 20f;
            while (runner.CurrentTaskFeedback?.Progress.Action != PlannerActionType.PrepareDrink &&
                outcomes.Count == 0 && Time.realtimeSinceStartup < until) yield return null;
            Assert.That(outcomes, Is.Empty);
            Assert.That(runner.CurrentTaskFeedback?.Progress.Action, Is.EqualTo(PlannerActionType.PrepareDrink));
            timing.preparing = Time.realtimeSinceStartupAsDouble;
            MovePlayer(merchant.transform.position + merchant.transform.right * 1.2f);
            Assert.That(dialogue.SelectTarget(merchant.GetComponent<DialogueParticipant>()), Is.True);

            while (Time.realtimeSinceStartupAsDouble - timing.preparing < 2d) yield return null;
            Assert.That(runner.HasActiveTask && driver.HasPendingActionOutcome, Is.True);
            Assert.That(outcomes, Is.Empty);
            Assert.That(world.WorldRevision, Is.EqualTo(revision));
            var chat = BeginUtterance("拿铁现在做得怎么样了？");
            var captured = chat.context.runtime_state;
            Assert.That(captured.status, Is.EqualTo("IN_PROGRESS"));
            Assert.That(captured.action_id, Is.EqualTo("prepare_drink"));
            Assert.That(captured.drink_type, Is.EqualTo("latte"));
            Assert.That(captured.step, Is.EqualTo("PrepareDrink"));
            Assert.That(captured.proposal_id, Is.EqualTo(proposal));
            Assert.That(captured.trace_id, Is.EqualTo(original.trace_id));
            Assert.That(captured.proposal_revision, Is.EqualTo(0));
            Debug.Log("[P1bContextWire] " + chat.Serialize());
            timing.chatAccepted = Time.realtimeSinceStartupAsDouble;
            timing.chatRun = orchestrator.CurrentRun.RunId;
            Assert.That(timing.chatRun, Is.Not.EqualTo(timing.originalRun));
            var progressReply = Response(chat, false);
            progressReply.decision.utterance = "我正在制作你的拿铁。";
            Assert.That(driver.TryConsumeResponse(progressReply), Is.True);
            AssertDialogueEvidence(chat, progressReply.decision.utterance);
            timing.chatPresented = Time.realtimeSinceStartupAsDouble;
            Assert.That(utterances.Last().RequestId, Is.EqualTo(chat.request_id));
            Assert.That(runner.ActiveProposalId, Is.EqualTo(proposal));
            Assert.That(outcomes, Is.Empty, "The new chat must actually be presented before the original coffee finishes.");

            // Keep a further foreground Run open across the original execution's terminal callback.
            var pending = BeginUtterance("再聊一句");
            var pendingRun = orchestrator.CurrentRun;
            dialogue.HideDialoguePresentation();
            timing.hidden = Time.realtimeSinceStartupAsDouble;
            yield return null;
            Assert.That(runner.HasActiveTask && driver.HasPendingActionOutcome, Is.True);
            if (cancel) runner.CancelCurrentPlan();
            if (fail) merchant.GetComponent<DrinkPreparationCapability>().Station.enabled = false;
            until = Time.realtimeSinceStartup + 18f;
            while (outcomes.Count == 0 && Time.realtimeSinceStartup < until) yield return null;
            Assert.That(outcomes, Has.Count.EqualTo(1));
            Assert.That(outcomes[0].ProposalId, Is.EqualTo(proposal));
            Assert.That(outcomes[0].TraceId, Is.EqualTo(original.trace_id));
            Assert.That(outcomes[0].Status, Is.EqualTo(fail ? PlanExecutionStatus.Failed : cancel ? PlanExecutionStatus.Cancelled : PlanExecutionStatus.Succeeded),
                outcomes[0].FailureCode);
            Assert.That(orchestrator.CurrentRun, Is.SameAs(pendingRun));
            Assert.That(pendingRun.State, Is.EqualTo(CognitiveRunState.Running));
            Assert.That(driver.PendingRequest, Is.SameAs(pending));
            Assert.That(driver.HasPendingActionOutcome, Is.False);
            Assert.That(world.GetDrinkCount(player.EntityId, "latte"), Is.EqualTo(drinks + (cancel || fail ? 0 : 1)));
            Assert.That(world.WorldRevision, Is.EqualTo(revision + (cancel || fail ? 0 : 1)));
            Assert.That(world.TryGetDrinkReceipt(merchant.GetComponent<WorldEntity>().EntityId, proposal,
                original.trace_id, out var receipt), Is.EqualTo(!cancel && !fail));
            if (!cancel && !fail)
            {
                Assert.That(outcomeAt - timing.preparing, Is.GreaterThanOrEqualTo(seconds - .1d));
                Assert.That(receipt.WorldRevision, Is.EqualTo(revision + 1));
                Assert.That(receipt.Player, Is.EqualTo(player.EntityId));
            }
            timing.outcome = outcomeAt;
            timing.status = outcomes[0].Status.ToString();
            Assert.That(timing.chatAccepted, Is.LessThan(timing.outcome));
            Assert.That(timing.chatPresented, Is.LessThan(timing.outcome));
            Debug.Log("[P1aCoffeeTiming] " + JsonUtility.ToJson(timing));
            // Let normal Unity updates continue, proving there is no second outcome/effect after settlement.
            double observeUntil = Time.realtimeSinceStartupAsDouble + .5d;
            while (Time.realtimeSinceStartupAsDouble < observeUntil) yield return null;
            Assert.That(outcomes, Has.Count.EqualTo(1));
            Assert.That(world.WorldRevision, Is.EqualTo(revision + (cancel || fail ? 0 : 1)));
            dialogue.ShowDialoguePresentation();
            int shown = utterances.Count, drafts = dialogueDrafts.Count;
            Assert.That(captured.status, Is.EqualTo("IN_PROGRESS"), "Captured snapshots are not live mutable state.");
            Assert.That(driver.TryConsumeResponse(Response(pending, false)), Is.False);
            Assert.That(pendingRun.State, Is.EqualTo(CognitiveRunState.Failed));
            Assert.That(utterances.Count, Is.EqualTo(shown));
            Assert.That(dialogueDrafts.Count, Is.EqualTo(drafts), "Expired output cannot become DialogueExperience.");
            var fresh = BeginUtterance("拿铁最后怎么样了？");
            Assert.That(fresh.context.runtime_state.status, Is.EqualTo(fail ? "FAILED" : cancel ? "CANCELLED" : "SUCCEEDED"));
            Assert.That(fresh.context.runtime_state.proposal_id, Is.EqualTo(proposal));
            Assert.That(fresh.context.runtime_state.outcome_id, Is.EqualTo(outcomes[0].OutcomeId));
            Debug.Log("[P1bContextWire] " + fresh.Serialize());
            var finalReply = Response(fresh, false);
            finalReply.decision.utterance = fail ? "这杯拿铁没能完成。" : cancel ? "这次制作已经取消。" : "你的拿铁已经做好了。";
            Assert.That(driver.TryConsumeResponse(finalReply), Is.True);
            AssertDialogueEvidence(fresh, finalReply.decision.utterance);
            Assert.That(originalRun.State, Is.EqualTo(CognitiveRunState.Completed));
        }

        private void AssertDialogueEvidence(CognitiveRequest request, string text)
        {
            var draft = dialogueDrafts.Last();
            Assert.That(draft.IsValid, Is.True);
            Assert.That(draft.RequestId, Is.EqualTo(request.request_id));
            Assert.That(draft.TraceId, Is.EqualTo(request.trace_id));
            Assert.That(draft.ConversationId, Is.EqualTo(dialogue.CurrentConversationId));
            Assert.That(draft.NaturalLanguagePayload, Is.EqualTo(text));
            Assert.That(draft.EventType, Is.EqualTo("dialogue_response"));
            Assert.That(Object.FindAnyObjectByType<DialogueBubbleManager>().GetMessages(
                merchant.GetComponent<WorldEntity>().EntityId).Last().Text, Is.EqualTo(text));
            Debug.Log("[P1bDialogueDraft] " + ExperienceAppendRequest.FromDraft(draft).Serialize());
        }

        [UnityTest]
        public IEnumerator P1bIdentityDisableAndLateOutcome_DoNotExposeOrRestoreExecution()
        {
            var original = BeginUtterance("把杯子拿给我");
            Assert.That(driver.TryConsumeResponse(Response(original, true)), Is.True);
            string proposal = runner.ActiveProposalId;
            var state = runner.CaptureExecutionState(player.EntityId);
            Assert.That(state.status, Is.EqualTo("IN_PROGRESS"));
            Assert.That(runner.CaptureExecutionState("player_other").status, Is.EqualTo("UNKNOWN"));
            Assert.That(runner.CaptureExecutionState("player_other").proposal_id, Is.Null);
            var other = Object.FindObjectsByType<WorldEntity>().Single(e => e.EntityId == "npc_cafe_regular_01");
            Assert.That(other.GetComponent<GoalPlanRunner>(), Is.Null);
            MovePlayer(other.transform.position + Vector3.forward);
            Assert.That(dialogue.SelectTarget(other.GetComponent<DialogueParticipant>()), Is.True);
            var otherSubmission = other.GetComponent<CognitiveDialogueSubmissionHandler>().Submit(new UserUtterance {
                conversationId = dialogue.CurrentConversationId, speakerEntityId = player.EntityId,
                targetAgentId = other.EntityId, inputMode = "text", text = "你在做拿铁吗？" });
            Assert.That(otherSubmission.Accepted, Is.True);
            var otherRequest = other.GetComponent<CognitiveContextComposer>().LastRequest;
            Assert.That(otherRequest.context.runtime_state.actor_entity_id, Is.EqualTo(other.EntityId));
            Assert.That(otherRequest.context.runtime_state.status, Is.EqualTo("UNKNOWN"));
            Assert.That(otherRequest.context.runtime_state.proposal_id, Is.Null);
            Debug.Log("[P1bContextWire] " + otherRequest.Serialize());
            other.GetComponent<AgentOrchestrator>().CancelCurrentRun();
            MovePlayer(merchant.transform.position + merchant.transform.right * 1.2f);
            Assert.That(dialogue.SelectTarget(merchant.GetComponent<DialogueParticipant>()), Is.True);
            string beforeReopen = dialogue.CurrentConversationId;
            dialogue.ClearSession();
            Assert.That(runner.HasActiveTask && driver.HasPendingActionOutcome, Is.True);
            Assert.That(dialogue.SelectTarget(merchant.GetComponent<DialogueParticipant>()), Is.True);
            Assert.That(dialogue.CurrentConversationId, Is.Not.EqualTo(beforeReopen));
            var reopened = BeginUtterance("你还在帮我拿杯子吗？");
            Assert.That(reopened.context.runtime_state.status, Is.EqualTo("IN_PROGRESS"));
            Assert.That(reopened.context.runtime_state.proposal_id, Is.EqualTo(proposal));
            Assert.That(reopened.context.runtime_state.trace_id, Is.EqualTo(original.trace_id));
            Assert.That(driver.TryConsumeResponse(Response(reopened, false)), Is.True);
            Assert.That(runner.ActiveProposalId, Is.EqualTo(proposal));
            var registry = Object.FindAnyObjectByType<EntityRegistry>();
            var saved = player.PersistentIdentity;
            Assert.Throws<System.InvalidOperationException>(() => player.Configure(player.EntityId, player.DefinitionId,
                EntityKind.Agent, AgentRole.Player, new PersistentIdentityRef("player", "Player", "different-player")));
            // Simulate corrupt legacy identity data to verify the projection's additional guard.
            // Actual Registry admission above forbids rebinding; this never authorizes an action.
            var identityField = typeof(WorldEntity).GetField("persistentIdentity", BindingFlags.Instance | BindingFlags.NonPublic);
            identityField.SetValue(player, new PersistentIdentityRef("player", "Player", "different-player"));
            Assert.That(runner.CaptureExecutionState(player.EntityId).status, Is.EqualTo("UNKNOWN"));
            identityField.SetValue(player, saved);
            var npc = merchant.GetComponent<WorldEntity>();
            saved = npc.PersistentIdentity;
            Assert.Throws<System.InvalidOperationException>(() => npc.Configure(npc.EntityId, npc.DefinitionId,
                EntityKind.Agent, AgentRole.Npc, new PersistentIdentityRef("npc", "Character", "different-npc")));
            identityField.SetValue(npc, new PersistentIdentityRef("npc", "Character", "different-npc"));
            Assert.That(runner.CaptureExecutionState(player.EntityId).status, Is.EqualTo("UNKNOWN"));
            identityField.SetValue(npc, saved);
            // A published result from another proposal is not authority to mutate the Runner's state.
            var late = new GoalOutcome("late", "proposal_old", 0, "old_trace", "bring_object", npc.EntityId,
                "cup_01", player.EntityId, PlanExecutionStatus.Succeeded, "", world.WorldRevision, Time.realtimeSinceStartupAsDouble);
            typeof(GoalPlanRunner).GetMethod("PublishGoalOutcome", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(runner, new object[] { (GoalOutcome?)late });
            Assert.That(runner.CaptureExecutionState(player.EntityId).proposal_id, Is.EqualTo(proposal));
            Assert.That(runner.CaptureExecutionState(player.EntityId).status, Is.EqualTo("IN_PROGRESS"));
            runner.enabled = false;
            Assert.That(runner.CaptureExecutionState(player.EntityId).status, Is.EqualTo("UNKNOWN"));
            runner.enabled = true;
            Assert.That(runner.CaptureExecutionState(player.EntityId).status, Is.EqualTo("CANCELLED"));
            yield return null;
        }

        [UnityTest]
        public IEnumerator P1bUnknownSnapshot_PlayerIdentityChangeRejectsLateReply()
        {
            var request = BeginUtterance("随便聊聊");
            var run = orchestrator.CurrentRun;
            Assert.That(request.context.runtime_state.status, Is.EqualTo("UNKNOWN"));
            var saved = player.PersistentIdentity;
            var field = typeof(WorldEntity).GetField("persistentIdentity", BindingFlags.Instance | BindingFlags.NonPublic);
            field.SetValue(player, new PersistentIdentityRef("player", "Player", "different-player"));
            try
            {
                Assert.That(driver.TryConsumeResponse(Response(request, false)), Is.False);
                Assert.That(run.State, Is.EqualTo(CognitiveRunState.Failed));
                Assert.That(utterances, Is.Empty);
                Assert.That(dialogueDrafts, Is.Empty);
            }
            finally { field.SetValue(player, saved); }
            var fresh = BeginUtterance("重新聊一句");
            Assert.That(driver.TryConsumeResponse(Response(fresh, false)), Is.True);
            yield return null;
        }

        [UnityTest]
        public IEnumerator P1bReloadSceneAndRuntimeSession_CannotResurrectAcceptedGoal()
        {
            var original = BeginUtterance("把杯子拿给我");
            Assert.That(driver.TryConsumeResponse(Response(original, true)), Is.True);
            var state = runner.CaptureExecutionState(player.EntityId);
            Assert.That(state.status, Is.EqualTo("IN_PROGRESS"));
            SessionState.SetString("P1b.Reload.Request", original.Serialize());
            SessionState.SetString("P1b.Reload.State", JsonUtility.ToJson(state));
            yield return new ExitPlayMode();
            // EditMode transition instructions must be yielded directly, not hidden in a nested setup enumerator.
            PrepareScene();
            yield return new EnterPlayMode();
            BindScene();
            // Domain reload deliberately destroys runtime objects and iterator-local DTO references.
            // SessionState is test-only Editor evidence, never game state or a production recovery path.
            state = JsonUtility.FromJson<CognitiveExecutionState>(SessionState.GetString("P1b.Reload.State", ""));
            original = Newtonsoft.Json.JsonConvert.DeserializeObject<CognitiveRequest>(SessionState.GetString("P1b.Reload.Request", ""));
            var fresh = BeginUtterance("我刚才的任务还在执行吗？");
            Assert.That(fresh.context.runtime_state.status, Is.EqualTo("UNKNOWN"));
            Assert.That(fresh.context.runtime_state.proposal_id, Is.Null);
            Assert.That(fresh.context.runtime_state.source_session_id, Is.Not.EqualTo(state.source_session_id));
            Assert.That(fresh.context.runtime_state.scene_instance_id, Is.Not.EqualTo(state.scene_instance_id));
            Assert.That(driver.TryConsumeResponse(Response(original, false)), Is.False);
            Assert.That(driver.PendingRequest, Is.SameAs(fresh));
            Debug.Log("[P1bContextWire] " + fresh.Serialize());
            Assert.That(driver.TryConsumeResponse(Response(fresh, false)), Is.True);
        }

        [UnityTest]
        public IEnumerator Control_Cappuccino_StopActuallyCancels() => RunControlCancel("cappuccino", "不对，先别做了");

        [UnityTest]
        public IEnumerator Control_Latte_StopMeansCancelNotSuspend() => RunControlCancel("latte", "先停一下");

        private IEnumerator RunControlCancel(string drink, string text)
        {
            var original = BeginControlCoffee(drink, 12f);
            float until = Time.realtimeSinceStartup + 8;
            while (runner.CurrentTaskFeedback?.Progress.Action != PlannerActionType.PrepareDrink && Time.realtimeSinceStartup < until) yield return null;
            Assert.That(runner.CurrentTaskFeedback?.Progress.Action, Is.EqualTo(PlannerActionType.PrepareDrink));
            yield return new WaitForSecondsRealtime(2);
            var request = BeginUtterance(text);
            Assert.That(request.context.runtime_state.status, Is.EqualTo("IN_PROGRESS"));
            Assert.That(request.context.runtime_state.allowed_controls, Does.Contain("cancel_active_goal"));
            var response = ControlReply(request);
            int drinks = world.GetDrinkCount(player.EntityId, drink), revision = world.WorldRevision;
            Assert.That(driver.TryConsumeResponse(response), Is.True, driver.LastActionControlResult);
            Assert.That(driver.LastActionControlResult, Is.EqualTo("cancelled"));
            Assert.That(outcomes, Has.Count.EqualTo(1));
            Assert.That(outcomes[0].Status, Is.EqualTo(PlanExecutionStatus.Cancelled));
            Assert.That(outcomes[0].ProposalId, Is.EqualTo(request.context.runtime_state.proposal_id));
            Assert.That(outcomes[0].TraceId, Is.EqualTo(original.trace_id));
            Assert.That(driver.HasPendingActionOutcome || runner.HasActiveTask, Is.False);
            var cancelledDraft = GoalOutcomeExperienceAdapter.Create(outcomes[0], Object.FindAnyObjectByType<EntityRegistry>(), world);
            Assert.That(cancelledDraft.IsValid, Is.True);
            Assert.That(cancelledDraft.EventType, Is.EqualTo("goal_cancelled"));
            AssertDialogueEvidence(request, response.decision.utterance);
            Assert.That(driver.TryConsumeResponse(response), Is.False, "Duplicate control response is inert.");
            Executor().Tick(20f); yield return null;
            Assert.That(outcomes, Has.Count.EqualTo(1));
            Assert.That(world.GetDrinkCount(player.EntityId, drink), Is.EqualTo(drinks));
            Assert.That(world.WorldRevision, Is.EqualTo(revision));
            Assert.That(world.TryGetDrinkReceipt(merchant.GetComponent<WorldEntity>().EntityId, outcomes[0].ProposalId,
                original.trace_id, out _), Is.False);
            var followup = BeginUtterance("刚才那杯怎么样了？");
            Assert.That(followup.context.runtime_state.status, Is.EqualTo("CANCELLED"));
            Assert.That(followup.context.runtime_state.outcome_id, Is.EqualTo(outcomes[0].OutcomeId));
            var answer = Response(followup, false); answer.decision.utterance = "刚才那杯已经取消了，没有继续做。";
            Assert.That(driver.TryConsumeResponse(answer), Is.True);
            AssertDialogueEvidence(followup, answer.decision.utterance);
            Debug.Log("[GoalControlAcceptance] " + request.Serialize());
        }

        [UnityTest]
        public IEnumerator Control_CompletionWinsRace_TruthfulReplyAndOneSuccess()
        {
            BeginControlCoffee("latte", 12f);
            float until=Time.realtimeSinceStartup+8;
            while(runner.CurrentTaskFeedback?.Progress.Action!=PlannerActionType.PrepareDrink && Time.realtimeSinceStartup<until) yield return null;
            var request=BeginUtterance("不做了"); var response=ControlReply(request);
            Executor().Tick(20f); yield return null;
            Assert.That(outcomes,Has.Count.EqualTo(1));
            Assert.That(outcomes[0].Status,Is.EqualTo(PlanExecutionStatus.Succeeded));
            Assert.That(driver.TryConsumeResponse(response),Is.True);
            Assert.That(driver.LastActionControlResult,Is.EqualTo("already_completed"));
            Assert.That(utterances.Last().Utterance,Is.Not.EqualTo(response.decision.utterance));
            AssertDialogueEvidence(request,utterances.Last().Utterance);
            Assert.That(outcomes,Has.Count.EqualTo(1));
            Assert.That(runner.CaptureExecutionState(player.EntityId).status,Is.EqualTo("SUCCEEDED"));
            Assert.That(driver.TryConsumeResponse(response),Is.False);
        }

        [UnityTest]
        public IEnumerator Control_AlreadyCancelled_DoesNotPublishAgain()
        {
            BeginControlCoffee("latte",12f); yield return null;
            var request=BeginUtterance("取消这杯");var response=ControlReply(request);
            runner.CancelCurrentPlan();
            Assert.That(driver.TryConsumeResponse(response),Is.True);
            Assert.That(driver.LastActionControlResult,Is.EqualTo("already_cancelled"));
            Assert.That(outcomes,Has.Count.EqualTo(1));
            Assert.That(outcomes[0].Status,Is.EqualTo(PlanExecutionStatus.Cancelled));
        }

        [UnityTest]
        public IEnumerator Control_OrdinaryChatAndProgressNeverCancel()
        {
            BeginControlCoffee("latte",12f);yield return null;
            foreach(var text in new[]{"你喜欢画什么？","现在在做什么？","不要取消，继续做"})
            {
                var request=BeginUtterance(text);
                Assert.That(driver.TryConsumeResponse(Response(request,false)),Is.True);
                Assert.That(runner.HasActiveTask,Is.True);Assert.That(outcomes,Is.Empty);
            }
            float until=Time.realtimeSinceStartup+8;
            while(runner.CurrentTaskFeedback?.Progress.Action!=PlannerActionType.PrepareDrink && Time.realtimeSinceStartup<until)yield return null;
            Executor().Tick(20f);yield return null;
            Assert.That(outcomes.Single().Status,Is.EqualTo(PlanExecutionStatus.Succeeded));
        }

        [UnityTest]
        public IEnumerator Control_HiddenInputStillCancelsOriginalGoal()
        {
            // Previous UI-focus denial was retired: accepted structured control is bound to
            // the original request/execution, never to whichever input panel is currently open.
            BeginControlCoffee("latte",12f); yield return null;
            var request=BeginUtterance("别做了");var response=ControlReply(request);
            dialogue.HideDialoguePresentation();
            Assert.That(driver.TryConsumeResponse(response),Is.True);
            Assert.That(runner.HasActiveTask,Is.False);
            Assert.That(outcomes.Single().Status,Is.EqualTo(PlanExecutionStatus.Cancelled));
            Assert.That(dialogue.IsPresentationVisible,Is.False);
        }

        [UnityTest]
        public IEnumerator Control_SwitchedInputStillCancelsOnlyOriginalGoal()
        {
            BeginControlCoffee("latte",12f); yield return null;
            var request=BeginUtterance("停下");var response=ControlReply(request);
            var other=Object.FindObjectsByType<DialogueParticipant>().First(p=>p.Entity!=merchant.GetComponent<WorldEntity>() && p.Entity.IsAgent(AgentRole.Npc));
            MovePlayer(other.transform.position+other.transform.right*1.2f);
            Assert.That(dialogue.SelectTarget(other),Is.True);
            string conversation=dialogue.CurrentConversationId;
            Assert.That(driver.TryConsumeResponse(response),Is.True);
            Assert.That(outcomes.Single().Status,Is.EqualTo(PlanExecutionStatus.Cancelled));
            Assert.That(dialogue.SelectedTarget,Is.SameAs(other));
            Assert.That(dialogue.CurrentConversationId,Is.EqualTo(conversation));
        }

        [UnityTest]
        public IEnumerator Control_WrongProposalAndOldResponseCannotStopReplacement()
        {
            BeginControlCoffee("latte",12f);yield return null;
            var request=BeginUtterance("取消");var response=ControlReply(request);
            response.decision.action_control.trace_id="other_trace";
            Assert.That(driver.TryConsumeResponse(response),Is.False);
            Assert.That(runner.HasActiveTask,Is.True);Assert.That(outcomes,Is.Empty);
            request=BeginUtterance("取消");response=ControlReply(request);
            runner.CancelCurrentPlan();
            Assert.That(driver.TryConsumeResponse(response),Is.True);
            BeginControlCoffee("cappuccino",12f);
            var next=BeginUtterance("聊聊别的");var newRun=orchestrator.CurrentRun;
            Assert.That(driver.TryConsumeResponse(response),Is.False);
            Assert.That(orchestrator.CurrentRun,Is.SameAs(newRun));
            Assert.That(runner.HasActiveTask,Is.True);
            Assert.That(runner.CaptureExecutionState(player.EntityId).drink_type,Is.EqualTo("cappuccino"));
            Assert.That(driver.TryConsumeResponse(Response(next,false)),Is.True);
        }

        [UnityTest]
        public IEnumerator Control_ForeignScopeAndSavedTaskPolicyAreNotCancelled()
        {
            BeginControlCoffee("latte",12f);yield return null;
            var expected=runner.CaptureExecutionState(player.EntityId);
            Assert.That(runner.CaptureExecutionState("other_player").status,Is.EqualTo("UNKNOWN"));
            foreach(var field in new[]{"actor_entity_id","player_entity_id","source_session_id","scene_instance_id"})
            {
                var foreign=JsonUtility.FromJson<CognitiveExecutionState>(JsonUtility.ToJson(expected));
                typeof(CognitiveExecutionState).GetField(field).SetValue(foreign,"other_scope");
                Assert.That(runner.CancelOwnedGoal(foreign),Is.EqualTo("unconfirmed"));
                Assert.That(runner.HasActiveTask,Is.True);Assert.That(outcomes,Is.Empty);
            }
            var taskBridge=merchant.GetComponent<TaskExecutionBridge>()??merchant.gameObject.AddComponent<TaskExecutionBridge>();
            typeof(TaskExecutionBridge).GetField("ticket",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(taskBridge,
                new TaskExecutionTicketDto{trace_id=expected.trace_id,execution_request=new TaskExecutionRequestDto{proposal_id=expected.proposal_id,trace_id=expected.trace_id}});
            Assert.That(runner.CaptureExecutionState(player.EntityId).allowed_controls,Is.Null);
            Assert.That(runner.CancelOwnedGoal(expected),Is.EqualTo("not_allowed"));
            Assert.That(runner.HasActiveTask,Is.True);Assert.That(outcomes,Is.Empty);
            typeof(TaskExecutionBridge).GetField("ticket",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(taskBridge,null);
        }

        [UnityTest]
        public IEnumerator Control_PresentationFailureDoesNotUndoCancellationOrInventDialogue()
        {
            BeginControlCoffee("latte",12f);yield return null;
            var request=BeginUtterance("不做了");var response=ControlReply(request);
            int shown=utterances.Count,drafts=dialogueDrafts.Count;
            var field=typeof(CognitiveRunDriver).GetField("presentationBehaviour",BindingFlags.Instance|BindingFlags.NonPublic);
            var presenter=field.GetValue(driver);field.SetValue(driver,null);
            try
            {
                Assert.That(driver.TryConsumeResponse(response),Is.False);
                Assert.That(driver.LastActionControlResult,Is.EqualTo("cancelled"));
                Assert.That(outcomes.Single().Status,Is.EqualTo(PlanExecutionStatus.Cancelled));
                Assert.That(runner.HasActiveTask,Is.False);
                Assert.That(utterances,Has.Count.EqualTo(shown));Assert.That(dialogueDrafts,Has.Count.EqualTo(drafts));
            }
            finally{field.SetValue(driver,presenter);}
        }

        [Test]
        public void Control_StrictWireRejectsMalformedAndUnsupportedOperations()
        {
            foreach(var token in new[]{"true","\"0\"","-1"})
            {
                string json="{\"message_type\":\"cognitive_response\",\"schema_version\":\"1.0\",\"request_id\":\"r\",\"trace_id\":\"t\",\"run_id\":\"u\",\"agent_entity_id\":\"npc_cafe_01\",\"decision\":{\"decision_kind\":\"DIRECT_RESPONSE\",\"utterance\":\"好\",\"action_control\":{\"operation\":\"cancel_active_goal\",\"proposal_id\":\"p\",\"trace_id\":\"t\",\"proposal_revision\":"+token+"}}}";
                Assert.That(CognitiveResponseValidator.TryParse(json,out _,out _),Is.False);
            }
        }

        private PlanExecutor Executor() => (PlanExecutor)typeof(GoalPlanRunner).GetField("executor",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(runner);
        private CognitiveRequest BeginControlCoffee(string drink,float seconds)
        {
            var set=typeof(PlanExecutor).GetField("runnerSet",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(Executor());
            var runners=(Dictionary<PlannerActionType,IPlanActionRunner>)typeof(PlanActionRunnerSet).GetField("runners",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(set);
            runners[PlannerActionType.PrepareDrink]=new PrepareDrinkActionRunner(seconds);
            var request=BeginUtterance("请做一杯"+drink);var response=Response(request,true);
            response.decision.action_proposal=new CognitiveActionProposal{action_id="prepare_drink",bindings=new Dictionary<string,string>{{"drink_type",drink}}};
            response.validated_goal=new GoalProposal{type="prepare_drink",drink_type=drink};
            Assert.That(driver.TryConsumeResponse(response),Is.True,driver.LastActionHandoffResult);
            return request;
        }
        private static CognitiveResponse ControlReply(CognitiveRequest request)
        {
            var response=Response(request,false);var state=request.context.runtime_state;
            response.decision.utterance="好，这次就不做了。之后想喝的话再告诉我。";
            response.decision.action_control=new CognitiveActionControl{operation="cancel_active_goal",proposal_id=state.proposal_id,
                proposal_revision=state.proposal_revision,trace_id=state.trace_id};
            Assert.That(CognitiveResponseValidator.TryParse((string)typeof(CognitiveRequest).GetMethod("SerializeFields", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { response }),out var parsed,out var error),Is.True,error);
            return parsed;
        }

        [System.Serializable]
        private sealed class CoffeeTiming
        {
            public string scenario, proposal, trace, originalRun, chatRun, status;
            public float seconds;
            public double accepted, preparing, chatAccepted, chatPresented, hidden, outcome;
        }

        private CognitiveRequest BeginUtterance(string text)
        {
            driver.enabled = false; // Composer captures normally; transport never sends.
            var result = merchant.Submit(new UserUtterance { conversationId = dialogue.CurrentConversationId,
                speakerEntityId = player.EntityId, targetAgentId = merchant.GetComponent<WorldEntity>().EntityId,
                inputMode = "text", text = text });
            Assert.That(result.Accepted, Is.True, result.Status);
            var request = merchant.GetComponent<CognitiveContextComposer>().LastRequest;
            driver.enabled = true;
            Assert.That(driver.TryOwnRequest(request), Is.True);
            return request;
        }

        private static CognitiveResponse Response(CognitiveRequest request, bool action) => new()
        {
            message_type = "cognitive_response", schema_version = request.schema_version,
            task_result = request.schema_version == "1.1" ? "none" : null,
            request_id = request.request_id, trace_id = request.trace_id,
            run_id = request.run.run_id, agent_entity_id = request.run.agent_entity_id,
            decision = new CognitiveDecision { decision_kind = action ? "ACTION_PROPOSAL" : "DIRECT_RESPONSE",
                utterance = action ? "我去拿杯子。" : "我喜欢画街头的招牌。", emotion = "neutral", gesture = "none",
                action_proposal = action ? new CognitiveActionProposal { action_id = "bring_object",
                    bindings = new Dictionary<string, string> { ["target"] = "cup_01", ["destination"] = "player_01" } } : null },
            validated_goal = action ? new GoalProposal { type = "bring_object", target_entity_id = "cup_01", destination_entity_id = "player_01" } : null,
        };

        private void MovePlayer(Vector3 position)
        {
            var controller = player.GetComponent<CharacterController>();
            controller.enabled = false; player.transform.position = position; controller.enabled = true;
            Physics.SyncTransforms();
        }
    }
}
