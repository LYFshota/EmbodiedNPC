using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CafeDemo;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Events;
using EmbodiedNPC.Perception;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    public sealed class CafeV2SceneFactsTests
    {
        [UnitySetUp] public IEnumerator Load()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
            var client = Object.FindAnyObjectByType<BackendWebSocketClient>();
            var config = new SerializedObject(client);
            config.FindProperty("serverUrl").stringValue = "ws://127.0.0.1:18199/ws";
            config.FindProperty("connectOnStart").boolValue = false;
            config.ApplyModifiedPropertiesWithoutUndo(); client.enabled = false;
            Object.FindAnyObjectByType<CafeDemoDeveloperPanel>().enabled = false;
            foreach (var driver in Object.FindObjectsByType<CognitiveRunDriver>()) driver.enabled = false;
            yield return new EnterPlayMode(); yield return new WaitForSecondsRealtime(.5f);
        }
        [UnityTearDown] public IEnumerator Close() { if (Application.isPlaying) yield return new ExitPlayMode(); }
        static CognitiveContextComposer Actor(string id) => Object.FindObjectsByType<CognitiveContextComposer>()
            .Single(x => x.GetComponent<WorldEntity>().EntityId == id);
        static CognitiveRequest Ask(CognitiveContextComposer actor, string suffix, string text = "可以坐你对面吗？")
        {
            actor.GetComponent<AgentOrchestrator>().CancelCurrentRun();
            var agentEvent = new AgentEvent("facts_event_" + suffix, AgentEventTypes.PlayerUtterance,
                "player_01", actor.GetComponent<WorldEntity>().EntityId, "",
                new Dictionary<string,string>{{"text",text},{"conversation_id","facts-conversation-"+suffix}},
                Time.realtimeSinceStartupAsDouble, 0, traceId:"facts-trace-"+suffix);
            Assert.That(Object.FindAnyObjectByType<AgentEventRouter>().Route(agentEvent), Is.EqualTo(1));
            var request = actor.LastRequest;
            Assert.That(request, Is.Not.Null); Assert.That(request.context.current_event.event_id, Is.EqualTo(agentEvent.EventId));
            Assert.That(request.IsValid, Is.True);
            string output = Environment.GetEnvironmentVariable("EMBODIEDNPC_SCENE_FACTS_EVIDENCE");
            if (!string.IsNullOrEmpty(output)) { Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output,"unity-"+suffix+".json"),JsonUtility.ToJson(request)); }
            return request;
        }
        [UnityTest] public IEnumerator RealScene_SeatsFollowBodies_AndVisibilityNeverBecomesGlobal()
        {
            var a = Actor("npc_cafe_regular_01"); var b = Actor("npc_cafe_companion_01");
            foreach (var actor in Object.FindObjectsByType<CognitiveContextComposer>())
            {
                var perceivable=actor.GetComponent<PerceivableEntity>(); Assert.That(perceivable,Is.Not.Null);
                Assert.That(perceivable.VisualAnchor,Is.Not.EqualTo(actor.transform));
                Assert.That(perceivable.DisplayName,Is.EqualTo(actor.CharacterDefinition.DisplayName));
            }
            var initial=Ask(a,"occupied");
            Assert.That(initial.context.perception.entities.Any(x=>x.entity_id=="npc_cafe_companion_01"&&x.kind=="npc"&&x.visibility=="current_visible"),Is.True);
            Assert.That(initial.context.scene_facts.self.posture,Is.EqualTo("seated"));
            var opposite=initial.context.scene_facts.observed_seats.Single(x=>x.relation_to_self=="opposite");
            Assert.That(opposite.occupancy,Is.EqualTo("occupied")); Assert.That(opposite.occupant_entity_id,Is.EqualTo("npc_cafe_companion_01"));
            var player=Object.FindObjectsByType<WorldEntity>().Single(x=>x.EntityId=="player_01");
            Vector3 playerPosition=player.transform.position; player.transform.position+=Vector3.right*20f; Physics.SyncTransforms();
            var rememberedPlayer=Ask(a,"remembered-player","你还记得我以前说过喜欢什么饮品吗？");
            var perceivedPlayer=rememberedPlayer.context.perception.entities.Single(x=>x.entity_id=="player_01");
            Assert.That(perceivedPlayer.kind,Is.EqualTo("player")); Assert.That(perceivedPlayer.visibility,Is.EqualTo("remembered"));
            player.transform.position=playerPosition; Physics.SyncTransforms();
            var vision=a.GetComponent<VisionSensor>(); vision.enabled=false;
            var stopped=Ask(a,"vision-stopped");
            Assert.That(stopped.context.scene_facts.observed_seats,Is.Empty);
            Assert.That(stopped.context.perception.entities.Any(x=>x.visibility=="current_visible"),Is.False);
            vision.enabled=true;
            Vector3 oldPosition=b.transform.position; b.transform.position += Vector3.right * 20f; Physics.SyncTransforms();
            var moved=Ask(a,"moved");
            Assert.That(moved.context.perception.entities.Any(x=>x.entity_id=="npc_cafe_companion_01"&&x.visibility=="current_visible"),Is.False);
            Assert.That(moved.context.scene_facts.observed_seats.Single(x=>x.relation_to_self=="opposite").occupancy,Is.EqualTo("empty"));
            Assert.That(moved.context.perception.entities.Single(x=>x.entity_id=="npc_cafe_companion_01").kind,Is.EqualTo("npc"),"Remembered keeps observed agent_role");
            b.transform.position=oldPosition;Physics.SyncTransforms();yield return null;
            Assert.That(Ask(a,"returned").context.scene_facts.observed_seats.Single(x=>x.relation_to_self=="opposite").occupancy,Is.EqualTo("occupied"));
            var wall=GameObject.CreatePrimitive(PrimitiveType.Cube);wall.transform.position=(a.transform.position+b.transform.position)*.5f+Vector3.up*.85f;
            wall.transform.localScale=new Vector3(.1f,2f,2f);Physics.SyncTransforms();
            var occluded=Ask(a,"occluded");
            Assert.That(occluded.context.perception.entities.Any(x=>x.entity_id=="npc_cafe_companion_01"&&x.visibility=="current_visible"),Is.False);
            Assert.That(occluded.context.scene_facts.observed_seats.Any(x=>x.relation_to_self=="opposite"),Is.False);
            Object.Destroy(wall);yield return null;Physics.SyncTransforms();
            b.gameObject.SetActive(false);Physics.SyncTransforms();
            Assert.That(Ask(a,"disabled").context.scene_facts.observed_seats.Single(x=>x.relation_to_self=="opposite").occupancy,Is.EqualTo("empty"));
            b.gameObject.SetActive(true);yield return null;
            var seatedAnimator=b.GetComponentsInChildren<Animator>().Single(x=>x.isActiveAndEnabled);
            seatedAnimator.enabled=false;Physics.SyncTransforms();
            var invalidPose=Ask(a,"pose-unknown");Assert.That(invalidPose.context.scene_facts.observed_seats.Single(x=>x.relation_to_self=="opposite").occupancy,Is.EqualTo("unknown"));
            seatedAnimator.enabled=true;
            foreach (var actor in Object.FindObjectsByType<CognitiveContextComposer>()) Ask(actor,"actor-"+actor.CharacterDefinition.CharacterId,"你觉得这里适合一个人休息吗？");
            Assert.That(Object.FindAnyObjectByType<BackendWebSocketClient>().IsConnected,Is.False);
        }
    }
}
