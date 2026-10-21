using System;
using System.Collections.Generic;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Events;
using EmbodiedNPC.Memory;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEngine;

namespace EmbodiedNPC.Tests.Editor
{
    public sealed class DialogueContinuationCancellationTests
    {
        private GameObject root;
        private AgentOrchestrator orchestrator;
        private CognitiveRunDriver driver;
        private CognitiveTestPresenter presenter;
        private CognitiveRequest request;
        private readonly List<ExperienceDraft> experiences = new();

        [SetUp] public void SetUp()
        {
            root = new GameObject("isolated-continuation-race");
            var entity = root.AddComponent<WorldEntity>();
            entity.Configure("npc_cafe_01", "character.npc.test", EntityKind.Agent, AgentRole.Npc);
            var receiver = root.AddComponent<AgentEventReceiver>(); receiver.Configure(entity);
            var bridge = root.AddComponent<WakeCognitiveTriggerBridge>(); bridge.Configure(receiver);
            orchestrator = root.AddComponent<AgentOrchestrator>(); orchestrator.Configure(entity, bridge);
            var composer = root.AddComponent<CognitiveContextComposer>(); composer.enabled = false;
            presenter = root.AddComponent<CognitiveTestPresenter>();
            driver = root.AddComponent<CognitiveRunDriver>(); driver.Configure(entity, orchestrator, composer, null, presenter);
            var trigger = new CognitiveTrigger("trigger_auto", CognitiveTriggerTypes.Event, entity.EntityId,
                "event_auto", 1, AgentEventTypes.DialogueContinue);
            Assert.That(orchestrator.TryStartRun(trigger, out var run), Is.True);
            var dto = CognitiveTriggerDto.From(trigger);
            request = new CognitiveRequest { request_id = "request_auto", trace_id = "trace_auto", run = CognitiveRunDto.From(run),
                trigger = dto, context = new CognitiveContextSnapshot { run_id = run.RunId, agent_entity_id = run.AgentEntityId,
                    trigger = dto, current_event = new CognitiveEventDto { event_id = "event_auto",
                        event_type = AgentEventTypes.DialogueContinue, source_entity_id = entity.EntityId,
                        target_entity_id = entity.EntityId, payload = new[] { new CognitiveKeyValueDto {
                            key = "conversation_id", value = "conversation_test" } } },
                    relevant_player = new CognitivePlayerDto { player_entity_id = "player_01", display_name = "玩家" },
                    character = new CognitiveCharacterDto { character_id = "test.character", display_name = "测试",
                        role_summary = "测试角色", speaking_style = "简洁", backstory = "测试", personality = "安静", boundaries = "守边界" },
                    perception = PerceptionSnapshotDto.Empty(entity.EntityId) } };
            Assert.That(driver.TryOwnRequest(request), Is.True);
            driver.UtterancePresented += new DialogueExperienceAdapter(experiences.Add).Handle;
        }
        [TearDown] public void TearDown() { UnityEngine.Object.DestroyImmediate(root); experiences.Clear(); }
        private CognitiveResponse Reply() => new() { message_type = "cognitive_response", schema_version = "1.0",
            request_id = request.request_id, trace_id = request.trace_id, run_id = request.run.run_id,
            agent_entity_id = request.run.agent_entity_id, decision = new CognitiveDecision {
                decision_kind = "DIRECT_RESPONSE", utterance = "补充一句。" } };

        [Test] public void BufferedReplyThenPlayerCancel_ReleasesRunWithoutPresentationOrExperience()
        {
            CognitiveRequestFeedback feedback = null;
            driver.RequestFeedback += value => feedback = value;
            var oldRun = orchestrator.CurrentRun;
            Assert.That(driver.TryConsumeResponse(Reply()), Is.True);
            Assert.That(presenter.Count, Is.Zero);
            Assert.That(driver.CancelContinuation("wrong_request"), Is.False);
            Assert.That(driver.CancelContinuation(request.request_id), Is.True);
            Assert.That(feedback.Status, Is.EqualTo("cancelled"));
            Assert.That(feedback.RequestId, Is.EqualTo(request.request_id));
            Assert.That(oldRun.State, Is.EqualTo(CognitiveRunState.Cancelled));
            Assert.That(driver.PendingRequest, Is.Null);
            Assert.That(driver.FlushContinuationResponse(), Is.False);
            Assert.That(driver.TryConsumeResponse(Reply()), Is.False);
            Assert.That(experiences, Is.Empty);
            Assert.That(orchestrator.TryStartRun(new CognitiveTrigger("player_trigger", CognitiveTriggerTypes.Event,
                "npc_cafe_01", "player_event", 2, AgentEventTypes.PlayerUtterance), out _), Is.True);
        }
        [Test] public void MissingSessionGate_DropsBufferedReply()
        {
            Assert.That(driver.TryConsumeResponse(Reply()), Is.True);
            Assert.That(driver.FlushContinuationResponse(), Is.False);
            Assert.That(orchestrator.HasActiveRun, Is.False);
            Assert.That(presenter.Count, Is.Zero); Assert.That(experiences, Is.Empty);
        }
        [Test] public void DisableCancelsOnlyAutonomousPendingRun()
        {
            driver.enabled = false;
            // EditMode objects do not receive runtime callbacks; exercise the actual disable handler.
            typeof(CognitiveRunDriver).GetMethod("OnDisable", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic).Invoke(driver, null);
            Assert.That(driver.PendingRequest, Is.Null); Assert.That(orchestrator.HasActiveRun, Is.False);
            Assert.That(driver.TryConsumeResponse(Reply()), Is.False);
            Assert.That(experiences, Is.Empty);
        }
        [Test] public void DuplicateCandidateIsRejectedBeforePresentation()
        {
            Assert.That(driver.TryConsumeResponse(Reply()), Is.True);
            Assert.That(driver.TryConsumeResponse(Reply()), Is.False);
            Assert.That(presenter.Count, Is.Zero); Assert.That(experiences, Is.Empty);
        }
    }
}
