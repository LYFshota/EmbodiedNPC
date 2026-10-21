using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CafeDemo;
using EmbodiedNPC.Cognition;
using NUnit.Framework;
using TMPro;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    // EditMode only: private data and an empty scene, with no Backend or NPC execution.
    public sealed class CafeNpcBackgroundLengthTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        [Test]
        public void CounterAndSave_KeepOversizedDraft_ThenRecoverAndRestore()
        {
            Scene scene = EditorSceneManager.NewPreviewScene();
            string directory = Path.Combine(Path.GetTempPath(), "embodiednpc-p4b-" + Guid.NewGuid().ToString("N"));
            CharacterDefinition definition = null;
            CafeNpcCharacterSettings page = null;
            try
            {
                var root = new GameObject("P4B isolated UI", typeof(RectTransform));
                root.SetActive(false);
                SceneManager.MoveGameObjectToScene(root, scene);
                page = root.AddComponent<CafeNpcCharacterSettings>();
                T Child<T>(string name) where T : Component
                {
                    var child = new GameObject(name, typeof(RectTransform));
                    child.transform.SetParent(root.transform, false);
                    return child.AddComponent<T>();
                }
                TMP_InputField Input(string field)
                {
                    var input = Child<TMP_InputField>(field);
                    var text = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
                    text.transform.SetParent(input.transform, false);
                    input.textComponent = text.GetComponent<TextMeshProUGUI>();
                    input.textViewport = (RectTransform)input.transform;
                    Set(page, field, input);
                    return input;
                }
                foreach (string field in new[] { "displayName", "roleSummary", "personality", "speakingStyle", "boundaries" }) Input(field);
                TMP_InputField background = Input("backstory");
                background.characterLimit = 600; // Existing serialized controls must not silently truncate.
                var count = Child<TextMeshProUGUI>("BackstoryLabel");
                var status = Child<TextMeshProUGUI>("Status");
                Set(page, "statusText", status);
                Set(page, "characterIdText", Child<TextMeshProUGUI>("Id"));
                var save = Child<Button>("Save");
                Set(page, "saveButton", save);
                Set(page, "restoreButton", Child<Button>("Restore"));
                var picker = Child<TMP_Dropdown>("Picker");
                picker.AddOptions(new List<string> { "隔离 NPC" });
                Set(page, "npcDropdown", picker);
                definition = ScriptableObject.CreateInstance<CharacterDefinition>();
                definition.Configure("cafe.merchant", "隔离 NPC", "店员", "默认背景", "开朗", "自然", "权限不变");
                var composer = Child<CognitiveContextComposer>("NPC");
                Set(composer, "characterDefinition", definition);
                ((List<CognitiveContextComposer>)typeof(CafeNpcCharacterSettings).GetField("candidates", Private).GetValue(page)).Add(composer);
                var store = new CharacterOverrideStore(Path.Combine(directory, CharacterOverrideStore.FileName));
                Set(page, "store", store);
                Call(page, "OnEnable");
                Call(page, "LoadSelected");
                Assert.That(background.characterLimit, Is.Zero);

                string legal = new string('背', 1199) + "🙂";
                background.text = legal + "超";
                Assert.That(background.text, Is.EqualTo(legal + "超"), "No silent input truncation.");
                Assert.That(count.text, Does.Contain("1201 / 1200"));
                Assert.That(status.text, Does.Contain("1200"));
                Assert.That(save.interactable, Is.False);
                Call(page, "Save");
                Assert.That(File.Exists(store.FilePath), Is.False, "A programmatic save must also reject overflow.");
                Call(page, "OnDisable");
                Call(page, "OnEnable");
                Assert.That(background.text, Is.EqualTo(legal + "超"), "Feedback refresh must preserve the draft.");

                background.text = legal;
                Assert.That(count.text, Does.Contain("1200 / 1200"));
                Assert.That(save.interactable, Is.True);
                Assert.That(status.text, Does.Not.Contain("超过"));
                save.onClick.Invoke();
                Assert.That(CharacterProfileResolver.Resolve(definition, new CharacterOverrideStore(store.FilePath)).backstory, Is.EqualTo(legal));
                Call(page, "RestoreDefault");
                Assert.That(store.TryGet(definition.CharacterId, out _), Is.False);
                Assert.That(background.text, Is.EqualTo(definition.Backstory));
                Assert.That(count.text, Does.Contain("4 / 1200"));
            }
            finally
            {
                if (page != null) Call(page, "OnDisable");
                if (definition != null) Object.DestroyImmediate(definition);
                EditorSceneManager.ClosePreviewScene(scene);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        private static void Set(object target, string field, object value) => target.GetType().GetField(field, Private).SetValue(target, value);
        private static void Call(object target, string method) => target.GetType().GetMethod(method, Private).Invoke(target, null);
    }
}
