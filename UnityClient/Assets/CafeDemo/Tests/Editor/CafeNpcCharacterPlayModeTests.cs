using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using CafeDemo;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Settings;
using EmbodiedNPC.World;
using NUnit.Framework;
using TMPro;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    public sealed class CafeNpcCharacterPlayModeTests
    {
        private string directory;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
            var client = new UnityEditor.SerializedObject(Object.FindAnyObjectByType<EmbodiedNPC.Communication.BackendWebSocketClient>());
            client.FindProperty("connectOnStart").boolValue = false;
            client.ApplyModifiedPropertiesWithoutUndo();
            yield return new EnterPlayMode();
            Object.FindAnyObjectByType<CafeDemoDeveloperPanel>().ApplyStartupConfig(
                new ModelConfigView { configured = true });
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory)) Directory.Delete(directory, true);
            if (Application.isPlaying) yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator NextRequestUsesOverride_ExistingRequestAndCapabilitiesStayFixed_NpcSelectionPrefersDialogue()
        {
            WorldEntity merchant = Object.FindObjectsByType<WorldEntity>(FindObjectsInactive.Exclude)
                .Single(x => x.EntityId == "npc_cafe_01");
            WorldEntity customer = Object.FindObjectsByType<WorldEntity>(FindObjectsInactive.Exclude)
                .Single(x => x.EntityId == "npc_cafe_regular_01");
            var composer = merchant.GetComponent<CognitiveContextComposer>();
            directory = Path.Combine(Path.GetTempPath(), "embodiednpc-character-run-" + Guid.NewGuid().ToString("N"));
            var store = new CharacterOverrideStore(Path.Combine(directory, CharacterOverrideStore.FileName));
            typeof(CognitiveContextComposer).GetField("characterOverrides",
                BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(composer, store);

            CognitiveRequest Build(string suffix)
            {
                var trigger = new CognitiveTrigger("character-trigger-" + suffix, CognitiveTriggerTypes.Event,
                    merchant.EntityId, null, 1d, "character_settings_test");
                var run = new CognitiveRun("character-run-" + suffix, merchant.EntityId,
                    trigger.TriggerId, trigger.TriggerType, 1d);
                Assert.That(composer.TryBuildRequest(run, trigger, out CognitiveRequest request), Is.True);
                return request;
            }

            var dialogue = Object.FindAnyObjectByType<DialogueController>();
            typeof(DialogueController).GetField("selectedTarget", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(dialogue, customer.GetComponent<DialogueParticipant>());
            var panel = Object.FindAnyObjectByType<CafeDemoDeveloperPanel>();
            panel.Open();
            panel.ShowNpc();
            var page = Object.FindAnyObjectByType<CafeNpcCharacterSettings>();
            Assert.That(page.SelectedCharacterId, Is.EqualTo("cafe.regular_customer"));
            var customerDefinition = customer.GetComponent<CognitiveContextComposer>().CharacterDefinition;
            Assert.That(Ui<TMP_InputField>(page, "backstory").text, Is.EqualTo(customerDefinition.Backstory));
            Assert.That(Ui<TMP_InputField>(page, "personality").text, Is.EqualTo(customerDefinition.Personality));
            Assert.That(Ui<TMP_InputField>(page, "boundaries").text, Is.EqualTo(customerDefinition.Boundaries));
            typeof(CafeNpcCharacterSettings).GetField("store", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(page, store);
            typeof(DialogueController).GetField("selectedTarget", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(dialogue, null);
            panel.ShowOverview();
            panel.ShowNpc();
            Assert.That(page.SelectedCharacterId, Is.EqualTo("cafe.merchant"));

            CognitiveRequest previous = Build("before");
            string capabilitySnapshot = JsonUtility.ToJson(previous.context.capabilities);
            string defaultStyle = composer.CharacterDefinition.SpeakingStyle;
            string previousProfile = JsonUtility.ToJson(previous.context.character);
            Assert.That(previous.context.character.speaking_style, Is.EqualTo(defaultStyle));
            string[] fields = { "displayName", "roleSummary", "backstory", "personality", "speakingStyle", "boundaries" };
            int[] limits = { 80, 240, 600, 600, 480, 600 };
            for (int i = 0; i < fields.Length; i++)
                Assert.That(Ui<TMP_InputField>(page, fields[i]).characterLimit, Is.EqualTo(limits[i]));
            Ui<TMP_InputField>(page, "displayName").text = "阿明";
            Ui<TMP_InputField>(page, "roleSummary").text = "社区咖啡馆店员";
            Ui<TMP_InputField>(page, "backstory").text = "学过咖啡制作。";
            Ui<TMP_InputField>(page, "personality").text = "温和但有点幽默。";
            Ui<TMP_InputField>(page, "speakingStyle").text = "有点毒舌，但回答简短。";
            Ui<TMP_InputField>(page, "boundaries").text = "不会编造共同经历或声称未完成的动作。";
            Ui<Button>(page, "saveButton").onClick.Invoke();
            Assert.That(File.Exists(store.FilePath), Is.True);
            CognitiveRequest next = Build("after");
            Assert.That(next.context.character.character_id, Is.EqualTo("cafe.merchant"));
            Assert.That(next.context.character.display_name, Is.EqualTo("阿明"));
            Assert.That(next.context.character.speaking_style, Is.EqualTo("有点毒舌，但回答简短。"));
            Assert.That(next.context.character.backstory, Is.EqualTo("学过咖啡制作。"));
            Assert.That(next.context.character.personality, Is.EqualTo("温和但有点幽默。"));
            Assert.That(next.context.character.boundaries, Is.EqualTo("不会编造共同经历或声称未完成的动作。"));
            Assert.That(previous.context.character.display_name, Is.EqualTo("林薇薇"));
            Assert.That(JsonUtility.ToJson(previous.context.character), Is.EqualTo(previousProfile));
            Assert.That(JsonUtility.ToJson(next.context.capabilities), Is.EqualTo(capabilitySnapshot));
            Assert.That(composer.CharacterDefinition.DisplayName, Is.EqualTo("林薇薇"));

            Ui<Button>(page, "restoreButton").onClick.Invoke();
            CognitiveRequest restored = Build("restored");
            Assert.That(restored.context.character.display_name, Is.EqualTo("林薇薇"));
            Assert.That(restored.context.character.speaking_style, Is.EqualTo(defaultStyle));
            Assert.That(Ui<TMP_InputField>(page, "backstory").text, Is.EqualTo(composer.CharacterDefinition.Backstory));
            Assert.That(Ui<TMP_InputField>(page, "personality").text, Is.EqualTo(composer.CharacterDefinition.Personality));
            Assert.That(Ui<TMP_InputField>(page, "boundaries").text, Is.EqualTo(composer.CharacterDefinition.Boundaries));
            Ui<Button>(page, "saveButton").onClick.Invoke();
            Assert.That(store.TryGet("cafe.merchant", out _), Is.False,
                "Saving unchanged defaults must not create a permanent user copy.");
            panel.Close();
            yield return null;
        }

        private static T Ui<T>(CafeNpcCharacterSettings page, string field) where T : Object
        {
            var control = typeof(CafeNpcCharacterSettings).GetField(field,
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(page) as T;
            Assert.That(control, Is.Not.Null, "Current serialized settings control: " + field);
            return control;
        }
    }
}
