using System;
using System.Collections.Generic;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Events;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEngine;

namespace EmbodiedNPC.Tests
{
    public sealed class CognitiveContextFoundationTests
    {
        private readonly List<UnityEngine.Object> objects = new();

        [TearDown]
        public void TearDown()
        {
            foreach (UnityEngine.Object value in objects)
                if (value != null) UnityEngine.Object.DestroyImmediate(value);
            objects.Clear();
        }

        [Test]
        public void MerchantDefinition_ValidatesWithFrozenCafeIdentity()
        {
            CharacterDefinition definition = Definition();

            Assert.That(definition.TryValidateConfiguration(out string error), Is.True, error);
            Assert.That(definition.CharacterId, Is.EqualTo("cafe.merchant"));
            Assert.That(definition.DisplayName, Is.EqualTo("店员"));
        }

        [Test]
        public void Receiver_RecoversCorrelatedEventById_NotLastEvent()
        {
            GameObject root = new("Merchant");
            objects.Add(root);
            WorldEntity entity = root.AddComponent<WorldEntity>();
            entity.Configure("npc_cafe_01", "character.npc.merchant", EntityKind.Agent, AgentRole.Npc);
            AgentEventReceiver receiver = root.AddComponent<AgentEventReceiver>();
            receiver.Configure(entity);
            AgentEvent expected = Event("event_expected", 1d);
            receiver.Receive(expected);
            receiver.Receive(Event("event_later", 2d));

            Assert.That(receiver.TryGetRecentEvent("event_expected", out AgentEvent actual), Is.True);
            Assert.That(actual, Is.SameAs(expected));
            Assert.That(actual, Is.Not.SameAs(receiver.LastEvent));
        }

        [Test]
        public void RequestBuildFailure_FailsOwnedRunAndAllowsSecondWake()
        {
            GameObject root = new("Merchant");
            objects.Add(root);
            WorldEntity entity = root.AddComponent<WorldEntity>();
            entity.Configure("npc_cafe_01", "character.npc.merchant", EntityKind.Agent, AgentRole.Npc);
            AgentEventReceiver receiver = root.AddComponent<AgentEventReceiver>();
            EventInterestProfile profile = ScriptableObject.CreateInstance<EventInterestProfile>();
            objects.Add(profile);
            profile.Configure(EventInterestDecision.Ignore);
            receiver.Configure(entity, profile: profile);
            WakeCognitiveTriggerBridge bridge = root.AddComponent<WakeCognitiveTriggerBridge>();
            bridge.Configure(receiver);
            AgentOrchestrator orchestrator = root.AddComponent<AgentOrchestrator>();
            orchestrator.Configure(entity, bridge);
            CognitiveContextComposer composer = root.AddComponent<CognitiveContextComposer>();
            composer.Configure(entity, null, orchestrator, bridge, receiver, null, null, null);

            receiver.Receive(new AgentEvent("utterance_1", AgentEventTypes.PlayerUtterance,
                "player_01", entity.EntityId, string.Empty, null, 1d));
            CognitiveRun first = orchestrator.LastRun;
            Assert.That(first, Is.Not.Null);
            Assert.That(composer.LastRequest, Is.Null);
            Assert.That(first.State, Is.EqualTo(CognitiveRunState.Failed));
            Assert.That(orchestrator.HasActiveRun, Is.False);

            receiver.Receive(new AgentEvent("utterance_2", AgentEventTypes.PlayerUtterance,
                "player_01", entity.EntityId, string.Empty, null, 2d));
            CognitiveRun second = orchestrator.LastRun;
            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(second.RunId, Is.Not.EqualTo(first.RunId));
            Assert.That(second.State, Is.EqualTo(CognitiveRunState.Failed));
            Assert.That(orchestrator.HasActiveRun, Is.False);
        }

        [Test]
        public void CounterWakeRunRequest_ContainsBoundedContextWithoutFakeUtterance()
        {
            CognitiveTrigger trigger = new("trigger_01", CognitiveTriggerTypes.Event, "npc_cafe_01",
                "event_01", 10d, AgentEventTypes.PlayerEnteredArea);
            CognitiveRun run = new("run_01", "npc_cafe_01", trigger.TriggerId, trigger.TriggerType, 10.1d);
            AgentEvent currentEvent = Event("event_01", 10d);
            CognitiveTriggerDto triggerDto = CognitiveTriggerDto.From(trigger);
            var context = new CognitiveContextSnapshot
            {
                run_id = run.RunId,
                agent_entity_id = run.AgentEntityId,
                trigger = triggerDto,
                current_event = CognitiveEventDto.From(currentEvent),
                character = CognitiveCharacterDto.From(Definition()),
                behavior_mode = new CognitiveBehaviorModeDto { mode_id = BehaviorModeIds.Working },
                relevant_player = new CognitivePlayerDto
                    { player_entity_id = "player_01", display_name = "玩家" },
                perception = PerceptionSnapshotDto.Empty("npc_cafe_01"),
            };
            var request = new CognitiveRequest
            {
                request_id = "request_01",
                trace_id = "trace_01",
                run = CognitiveRunDto.From(run),
                trigger = triggerDto,
                context = context,
            };

            Assert.That(request.IsValid, Is.True);
            Assert.That(context.character.character_id, Is.EqualTo("cafe.merchant"));
            Assert.That(context.character.backstory, Is.EqualTo("在咖啡馆工作几年。"));
            Assert.That(context.character.personality, Is.EqualTo("温和慢热。"));
            Assert.That(context.character.boundaries, Is.EqualTo("不编造共同经历。"));
            Assert.That(context.behavior_mode.mode_id, Is.EqualTo("working"));
            Assert.That(context.current_event.event_type, Is.EqualTo(AgentEventTypes.PlayerEnteredArea));
            Assert.That(context.current_event.area_entity_id, Is.EqualTo("area_cafe_counter"));
            Assert.That(context.relevant_player.player_entity_id, Is.EqualTo("player_01"));
            Assert.That(context.perception.observer_entity_id, Is.EqualTo("npc_cafe_01"));
            Assert.That(JsonUtility.ToJson(request), Does.Not.Contain("utterance"));
        }

        [Test]
        public void RecallIdentityWireProjection_UsesPersistentIdentityOnlyForDurableAgents()
        {
            GameObject playerRoot = new("Player");
            GameObject npcRoot = new("NPC");
            GameObject temporaryRoot = new("Temporary");
            objects.Add(playerRoot);
            objects.Add(npcRoot);
            objects.Add(temporaryRoot);
            WorldEntity player = playerRoot.AddComponent<WorldEntity>();
            player.Configure("player_01", "character.player", EntityKind.Agent, AgentRole.Player,
                new PersistentIdentityRef("player", "Player", "U10001"));
            WorldEntity npc = npcRoot.AddComponent<WorldEntity>();
            npc.Configure("npc_02", "character.npc", EntityKind.Agent, AgentRole.Npc,
                new PersistentIdentityRef("npc", "Character", "n2.uid"));
            WorldEntity temporary = temporaryRoot.AddComponent<WorldEntity>();
            temporary.Configure("npc_temp", "character.npc", EntityKind.Agent, AgentRole.Npc);

            CognitiveRecallSubjectIdentityDto playerDto = CognitiveRecallSubjectIdentityDto.From(player);
            CognitiveRecallSubjectIdentityDto npcDto = CognitiveRecallSubjectIdentityDto.From(npc);
            CognitiveRecallSubjectIdentityDto temporaryDto = CognitiveRecallSubjectIdentityDto.From(temporary);
            Assert.That(playerDto.IsValid && npcDto.IsValid && temporaryDto.IsValid, Is.True);
            Assert.That(playerDto.persistent_id, Is.EqualTo("U10001"));
            Assert.That(npcDto.persistent_id, Is.EqualTo("n2.uid"));
            Assert.That(temporaryDto.persistent_id, Is.Null);
            Assert.That(JsonUtility.ToJson(playerDto), Does.Contain("U10001"));
        }

        private CharacterDefinition Definition()
        {
            CharacterDefinition definition = ScriptableObject.CreateInstance<CharacterDefinition>();
            objects.Add(definition);
            definition.Configure("cafe.merchant", "店员", "咖啡馆店员，负责接待顾客", "在咖啡馆工作几年。", "温和慢热。", "友好、自然、简洁", "不编造共同经历。");
            return definition;
        }

        private static AgentEvent Event(string eventId, double occurredAt) => new(eventId,
            AgentEventTypes.PlayerEnteredArea, "player_01", string.Empty, "area_cafe_counter",
            null, occurredAt);
    }
}
