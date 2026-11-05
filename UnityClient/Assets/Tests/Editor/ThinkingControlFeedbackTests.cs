using EmbodiedNPC.Settings;
using NUnit.Framework;

namespace EmbodiedNPC.Tests
{
    public sealed class ThinkingControlFeedbackTests
    {
        [TestCase("THINKING_CONTROL_UNVERIFIED", "未配置", "no reasoning control field")]
        [TestCase("THINKING_DISABLE_NOT_HONORED", "已请求关闭", "requested off")]
        public void ThinkingFailure_ExplainsTheObservedCauseInBothLanguages(string code, string chinese, string english)
        {
            var dialogue = P0FeedbackText.Dialogue(code);
            var settings = P0FeedbackText.ModelError("OPENAI_COMPATIBLE_" + code);
            Assert.AreEqual(dialogue, settings);
            Assert.That(dialogue.Chinese, Does.Contain(chinese));
            Assert.That(dialogue.English, Does.Contain(english));
            Assert.That(dialogue.Chinese, Does.Contain("请"));
            Assert.That(dialogue.English, Does.Contain(code == "THINKING_CONTROL_UNVERIFIED" ? "Check" : "Ask"));
            Assert.That(dialogue.Chinese.Length, Is.LessThan(70));
            Assert.That(dialogue.English.Length, Is.LessThan(180));
        }

        [Test]
        public void ThinkingFailures_KeepProtocolMismatchSeparateFromMissingControl()
        {
            Assert.AreNotEqual(P0FeedbackText.Dialogue("THINKING_CONTROL_UNVERIFIED"),
                P0FeedbackText.Dialogue("THINKING_DISABLE_NOT_HONORED"));
        }
    }
}
