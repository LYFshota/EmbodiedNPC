using System;
using System.Collections;
using System.Reflection;
using EmbodiedNPC.Settings;
using NUnit.Framework;
using TMPro;
using UnityEngine;

namespace EmbodiedNPC.Tests.Editor
{
    public sealed class ContextPolicySettingsTests
    {
        private ModelProfileSettingsTests fixture;
        private FirstRunModelSetup form;
        private GameObject root;
        private bool previousEnglish;
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private object Get(string name) => typeof(FirstRunModelSetup).GetField(name, Private).GetValue(form);
        private void Set(string name, object value) => typeof(FirstRunModelSetup).GetField(name, Private).SetValue(form, value);
        private object Invoke(string name, params object[] args) => typeof(FirstRunModelSetup).GetMethod(name, Private).Invoke(form, args);
        private TMP_Dropdown Mode => (TMP_Dropdown)Get("recentMode");
        private TMP_InputField Target => (TMP_InputField)Get("recentTarget");
        private string State => ((TMP_Text)Get("recentState")).text;

        [SetUp] public void Setup()
        {
            previousEnglish = SettingsUiLanguage.IsEnglish;
            fixture = new ModelProfileSettingsTests(); fixture.Setup();
            root = (GameObject)typeof(ModelProfileSettingsTests).GetField("root", Private).GetValue(fixture);
            form = root.GetComponent<FirstRunModelSetup>();
            Set("client", root.AddComponent<ModelConfigClient>()); // No backend reference: HTTP is never opened.
            Invoke("EnsureCompactLayout");
            SettingsUiLanguage.SetEnglish(true);
            Invoke("ApplyContextPolicyView", View(8, 1), null, true);
        }

        [TearDown] public void Cleanup() { SettingsUiLanguage.SetEnglish(previousEnglish); fixture.Cleanup(); }

        private static ContextPolicyView View(int? n, int revision) => new ContextPolicyView
        {
            saved = new ContextPolicyDocument { schema_version = 1, revision = revision,
                config = new ContextPolicyConfig { mode = n.HasValue ? "manual" : "auto", target_messages = n } },
            applied = new ContextPolicyDocument { schema_version = 1, revision = revision,
                config = new ContextPolicyConfig { mode = n.HasValue ? "manual" : "auto", target_messages = n } },
            source = "persisted", hot_apply = true, saved_matches_applied = true
        };

        [Test] public void OpenLoadsBackendAppliedValue_AndSwitchingCreatesDraft()
        {
            Assert.AreEqual(1, Mode.value); Assert.AreEqual("8", Target.text);
            Assert.IsFalse((bool)Invoke("ContextPolicyDirty"));
            Mode.value = 0;
            Assert.IsFalse(Target.transform.parent.gameObject.activeSelf);
            Assert.IsTrue((bool)Invoke("ContextPolicyDirty"));
            Assert.That(State, Does.Contain("Unsaved"));
            Mode.value = 1; Target.text = "32";
            Assert.IsTrue(Target.transform.parent.gameObject.activeSelf);
            Assert.IsTrue((bool)Invoke("ContextPolicyDirty"));
            Assert.AreEqual(8, ((ContextPolicyView)Get("contextPolicyView")).applied.config.target_messages);
        }

        [TestCase("0", false)] [TestCase("-1", false)] [TestCase("65", false)]
        [TestCase("true", false)] [TestCase("8.0", false)] [TestCase("", false)]
        [TestCase("8", true)] [TestCase("16", true)] [TestCase("32", true)] [TestCase("64", true)]
        public void TargetValidation(string text, bool valid)
        { Assert.AreEqual(valid, ContextPolicyConfig.TryDraft(1, text, out _)); }

        [Test] public void InvalidTarget_PreventsBothSaves()
        {
            Target.SetTextWithoutNotify("65");
            Drive((IEnumerator)Invoke("SaveSettingsWithContext"));
            Assert.That(State, Does.Contain("CONTEXT_POLICY_VALUE_INVALID"));
            Assert.That(((TMP_Text)Get("statusText")).text, Does.Contain("not saved"));
            Assert.AreEqual(1, ((ContextPolicyView)Get("contextPolicyView")).applied.revision);
        }

        [Test] public void SaveConfirmationUpdatesApplied_ButFailureKeepsDraft()
        {
            var request = new ContextPolicySaveRequest { expected_revision = 1,
                config = new ContextPolicyConfig { mode = "manual", target_messages = 32 } };
            Target.text = "32";
            Invoke("ApplyContextPolicySaveResult", request, null, "BACKEND_UNAVAILABLE");
            Assert.AreEqual("32", Target.text); Assert.That(State, Does.Contain("unconfirmed"));
            Assert.That(State, Does.Contain("BACKEND_UNAVAILABLE"));
            Assert.AreEqual(8, ((ContextPolicyView)Get("contextPolicyView")).applied.config.target_messages);
            Invoke("ApplyContextPolicySaveResult", request, View(32, 2), null);
            Assert.IsFalse((bool)Invoke("ContextPolicyDirty"));
            Assert.That(State, Does.Contain("Manual, target 32"));
        }

        [Test] public void WrongSuccessPayloadNeverConfirmsApplication()
        {
            Target.text = "32";
            var request = new ContextPolicySaveRequest { expected_revision = 1,
                config = new ContextPolicyConfig { mode = "manual", target_messages = 32 } };
            Invoke("ApplyContextPolicySaveResult", request, View(16, 2), null);
            Assert.That(State, Does.Contain("CONTEXT_POLICY_APPLY_UNCONFIRMED"));
            Assert.AreEqual("32", Target.text);
            Assert.AreEqual(8, ((ContextPolicyView)Get("contextPolicyView")).applied.config.target_messages);
        }

        [Test] public void ReopenReloadsApplied_ReconnectPreservesUnsavedInput()
        {
            Target.text = "32";
            Invoke("ApplyContextPolicyView", View(16, 2), null, false);
            Assert.AreEqual("32", Target.text);
            Assert.That(State, Does.Contain("target 16"));
            Invoke("ApplyContextPolicyView", View(16, 2), null, true);
            Assert.AreEqual("16", Target.text); Assert.IsFalse((bool)Invoke("ContextPolicyDirty"));
            Invoke("ApplyContextPolicyView", null, "BACKEND_UNAVAILABLE", false);
            Assert.That(State, Does.Contain("disconnected"));
            Assert.AreEqual("16", Target.text);
        }

        [Test] public void ContextOnlySaveDoesNotStageModel_AndNetworkFailureIsExplicit()
        {
            Target.text = "32";
            Assert.IsFalse((bool)Invoke("HasUnsavedSettings"));
            Drive((IEnumerator)Invoke("SaveSettingsWithContext"));
            string status = ((TMP_Text)Get("statusText")).text;
            Assert.That(status, Does.Contain("Model unchanged"));
            Assert.That(status, Does.Contain("context unconfirmed"));
            Assert.AreEqual("32", Target.text);
        }

        [Test] public void ContextDraftDoesNotChangeModelAtomicCommand()
        {
            string before = Invoke("BuildCombinedProfileCommand").ToString();
            Target.text = "32"; Mode.value = 0;
            Assert.AreEqual(before, Invoke("BuildCombinedProfileCommand").ToString());
        }

        [Test] public void BothDraftsFail_ReportBothResourcesWithoutSuccess()
        {
            Target.text = "32";
            ((TMP_InputField)Get("mainModel")).text = "edited-model";
            ((TMP_InputField)Get("maxOutputTokens")).text = "0";
            Drive((IEnumerator)Invoke("SaveSettingsWithContext"));
            string status = ((TMP_Text)Get("statusText")).text;
            Assert.That(status, Does.Contain("Model save unconfirmed"));
            Assert.That(status, Does.Contain("context unconfirmed"));
            Assert.That(State, Does.Contain("Model settings:"));
            Assert.AreEqual("32", Target.text);
        }

        [Test] public void FirstSetupCompletionWaitsForPendingContextSave()
        {
            var visiblePanel = new GameObject("SyntheticVisiblePanel");
            try
            {
                Set("panel", visiblePanel); Set("contextSaveInProgress", true);
                Invoke("Complete");
                Assert.IsTrue(visiblePanel.activeSelf);
                Assert.IsTrue((bool)Get("completeAfterContextSave"));
                Set("contextSaveInProgress", false); Invoke("Complete");
                Assert.IsFalse(visiblePanel.activeSelf);
            }
            finally { Set("panel", root); UnityEngine.Object.DestroyImmediate(visiblePanel); }
        }

        [Test] public void ClientSerializesAutoNull_AndReportsDisconnected()
        {
            string json = ModelConfigClient.SerializeContextPolicyRequest(new ContextPolicySaveRequest { expected_revision = 0, config = new ContextPolicyConfig() });
            Assert.That(json, Does.Contain("\"target_messages\":null"));
            Assert.That(json, Does.Not.Contain("IsValid"), "DTO helper properties are not part of the strict Backend schema.");
            var client = (ModelConfigClient)Get("client");
            string error = null;
            Drive(client.GetContextPolicy((value, code) => { Assert.IsNull(value); error = code; }));
            Assert.AreEqual("BACKEND_UNAVAILABLE", error);
        }

        [Test] public void BilingualHelpLivesInVisibleRetentionGroup()
        {
            // P3-A promotes retention out of the generation-parameter foldout.
            var retention = (Transform)Get("recentDialogueGroup");
            Assert.IsTrue(Mode.transform.IsChildOf(retention));
            Assert.IsFalse(retention.IsChildOf(((GameObject)Get("advancedSettings")).transform));
            Invoke("RefreshContextPolicy");
            Assert.That(((TMP_Text)Get("recentHelp")).text, Does.Contain("CurrentTask"));
            SettingsUiLanguage.SetEnglish(false); Invoke("RefreshContextPolicy");
            Assert.That(((TMP_Text)Get("recentHelp")).text, Does.Contain("最多加两条"));
            Assert.That(((TMP_Text)Get("recentTitle")).text, Does.Contain("近期对话保留"));
        }

        private static void Drive(IEnumerator operation)
        {
            while (operation.MoveNext())
                if (operation.Current is IEnumerator child) Drive(child);
                else Assert.IsNull(operation.Current, "Only disconnected, synchronous HTTP paths belong in this fixture.");
        }
    }
}
