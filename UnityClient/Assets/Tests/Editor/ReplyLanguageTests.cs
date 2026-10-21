using System;
using System.IO;
using System.Linq;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Settings;
using System.Text.RegularExpressions;
using NUnit.Framework;

public sealed class ReplyLanguageTests
{
    [Test] public void NpcBubbleRetainsLegalWhitespaceAndFullText()
    {
        var root = new UnityEngine.GameObject("LanguageBubbleContract", typeof(UnityEngine.RectTransform));
        root.SetActive(false);
        try
        {
            var manager = root.AddComponent<EmbodiedNPC.Dialogue.Presentation.DialogueBubbleManager>();
            string value = "  " + new string('a', 210) + " 😀\n";
            var message = manager.PushReply("npc_test", value, "request", "trace", "conversation");
            Assert.AreEqual(value, message.Text);
            Assert.AreEqual(value, manager.GetMessages("npc_test").Single().Text);
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }

    [TestCase("zh-CN")]
    [TestCase("en-US")]
    public void ModeFallbackUsesCapturedRunLocale(string language)
    {
        var method = typeof(CognitiveRunDriver).GetMethod("ModeStatus", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        var decision = (CognitiveDecision)method.Invoke(null, new object[] {null, false, true, language});
        Assert.AreEqual(language == "en-US", decision.utterance.StartsWith("I couldn't"));
        Assert.IsTrue(ReplyUtteranceContract.Fits(decision.utterance, language));
        Assert.IsNull(decision.action_proposal);
    }

    [TestCase(NpcReplyLanguagePreference.FollowInterface, false, "zh-CN")]
    [TestCase(NpcReplyLanguagePreference.FollowInterface, true, "en-US")]
    [TestCase(NpcReplyLanguagePreference.Chinese, true, "zh-CN")]
    [TestCase(NpcReplyLanguagePreference.English, false, "en-US")]
    public void PreferenceResolution(NpcReplyLanguagePreference preference, bool ui, string expected) =>
        Assert.AreEqual(expected, NpcReplyLanguageSettings.Resolve(preference, ui));

    [Test] public void TurnSessionResetAndFourNpcIsolation()
    {
        var npcs = Enumerable.Range(0, 4).Select(_ => new ReplyLanguageSession()).ToArray();
        Assert.AreEqual("en-US", npcs[0].Resolve(null, "接下来用中文交流", "en-US"), "Missing reliable identity uses global fallback");
        Assert.AreEqual("en-US", npcs[0].Resolve("player1:npc1:session1", null, "en-US"));
        Assert.AreEqual("zh-CN", npcs[0].Resolve("player1:npc1:session1", "这句话用中文回答", "en-US"));
        Assert.AreEqual("en-US", npcs[0].Resolve("player1:npc1:session1", "OK", "en-US"));
        Assert.AreEqual("zh-CN", npcs[0].Resolve("player1:npc1:session1", "接下来用中文交流", "en-US"));
        Assert.AreEqual("zh-CN", npcs[0].Resolve("player1:npc1:session1", null, "en-US"), "Continuation inherits session override");
        Assert.AreEqual("zh-CN", npcs[0].Resolve("player1:npc1:session1", "sure", "en-US"));
        for (int i = 1; i < 4; i++) Assert.AreEqual("en-US", npcs[i].Resolve($"player1:npc{i}:session1", null, "en-US"));
        Assert.AreEqual("en-US", npcs[0].Resolve("player2:npc1:session1", "OK", "en-US"));
        Assert.AreEqual("zh-CN", npcs[0].Resolve("player2:npc1:session1", "From now on, speak Chinese.", "en-US"));
        Assert.AreEqual("en-US", npcs[0].Resolve("player2:npc1:session1", "From now on, speak English.", "zh-CN"));
        Assert.AreEqual("zh-CN", npcs[0].Resolve("player2:npc1:session2", null, "zh-CN"));
        Assert.AreEqual("en-US", npcs[0].Resolve("player2:npc1:session2", null, "en-US"), "New run follows latest default");
    }

    [TestCase("他说：接下来用中文交流")]
    [TestCase("不要用中文回答")]
    [TestCase("Can you explain the words 'speak Chinese'?")]
    public void AmbiguousOrQuotedProseDoesNotPersist(string text)
    {
        var state = new ReplyLanguageSession();
        state.Resolve("session", text, "en-US");
        Assert.AreEqual("en-US", state.Resolve("session", "OK", "en-US"));
    }

    [TestCase("zh-CN", 100, 150)]
    [TestCase("en-US", 200, 250)]
    public void ScalarLengthNoTruncation(string language, int soft, int hard)
    {
        foreach (int count in new[] {soft, soft + 1, hard})
        {
            string value = string.Concat(Enumerable.Repeat("😀", count));
            Assert.AreEqual(count, ReplyUtteranceContract.ScalarCount(value));
            Assert.IsTrue(ReplyUtteranceContract.Fits(value, language));
            var decision = new CognitiveDecision { decision_kind = "DIRECT_RESPONSE", utterance = value };
            Assert.IsTrue(decision.IsValid); Assert.AreEqual(value, decision.utterance);
        }
        Assert.IsFalse(ReplyUtteranceContract.Fits(new string('a', hard + 1), language));
        Assert.IsFalse(ReplyUtteranceContract.Fits("\ud800", language));
        Assert.AreEqual(2, ReplyUtteranceContract.ScalarCount("e\u0301"), "Scalars, not grapheme clusters");
    }

    [Test] public void PortablePreferenceBackCompatibilityAndPersistence()
    {
        string directory = Path.Combine(Path.GetTempPath(), "NpcLanguage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string app = Path.Combine(directory, "Game", "EmbodiedNPC.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(app)); File.WriteAllText(app, "fixture");
            var paths = PortablePathSet.Open(Path.Combine(directory, "UserData"), app);
            var settings = new PortablePreferenceFile(paths);
            Assert.IsNull(settings.Error); Assert.AreEqual(0, settings.NpcReplyLanguage);
            settings.SetLanguage(true); Assert.IsTrue(settings.SetNpcReplyLanguage(1));
            var reopened = new PortablePreferenceFile(paths);
            Assert.AreEqual(1, reopened.NpcReplyLanguage); Assert.AreEqual(1, reopened.Language);
            Assert.IsFalse(reopened.SetNpcReplyLanguage(3));
            string json = Regex.Replace(File.ReadAllText(paths.SettingsFile), @",\s*""npc_reply_language""\s*:\s*[012]", "");
            File.WriteAllText(paths.SettingsFile, json);
            Assert.AreEqual(0, new PortablePreferenceFile(paths).NpcReplyLanguage);
            Assert.AreEqual(1, new PortablePreferenceFile(paths).Language);
        }
        finally { Directory.Delete(directory, true); } // Own GUID fixture directory only.
    }
}
