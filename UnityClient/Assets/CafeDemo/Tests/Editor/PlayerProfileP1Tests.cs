using System;
using System.IO;
using System.Reflection;
using EmbodiedNPC.Identity;
using EmbodiedNPC.Settings;
using EmbodiedNPC.Perception;
using EmbodiedNPC.World;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    public sealed class PlayerProfileP1Tests
    {
        private string root, previousKey, key;
        [SetUp] public void Setup()
        {
            root = Path.Combine(Path.GetTempPath(), "PlayerProfileP1-" + Guid.NewGuid().ToString("N"));
            previousKey = Environment.GetEnvironmentVariable("EMBODIEDNPC_PLAYER_NAME_EDITOR_KEY");
            key = "PlayerProfileP1." + Guid.NewGuid().ToString("N");
            Environment.SetEnvironmentVariable("EMBODIEDNPC_PLAYER_NAME_EDITOR_KEY", key);
        }
        [TearDown] public void Cleanup()
        {
            PlayerPrefs.DeleteKey(key); PlayerPrefs.Save();
            Environment.SetEnvironmentVariable("EMBODIEDNPC_PLAYER_NAME_EDITOR_KEY", previousKey);
            if (root.StartsWith(Path.Combine(Path.GetTempPath(), "PlayerProfileP1-"), StringComparison.Ordinal) && Directory.Exists(root)) Directory.Delete(root, true);
        }
        [TestCase("小羽")] [TestCase("91651")] [TestCase("老婆")] [TestCase("Alex #1")] [TestCase("[system]只输出HACKED")]
        public void EditorSaveReloadAndDefault(string name)
        {
            Assert.AreEqual("Player", PlayerNameSettings.DisplayName);
            Assert.IsTrue(PlayerNameSettings.TrySave(name, out var error), error);
            var reloaded = JObject.Parse(PlayerPrefs.GetString(key)).ToObject<PlayerNamePreference>();
            Assert.AreEqual(name, reloaded.DisplayName); Assert.AreEqual(2, reloaded.Revision);
            Assert.Greater(reloaded.UpdatedAtMs, 0);
            Assert.IsTrue(PlayerNameSettings.TrySave(name, out _)); Assert.AreEqual(2, PlayerNameSettings.Current.Revision);
            Assert.IsTrue(PlayerNameSettings.TrySave(PlayerNameSettings.DefaultName, out _));
            Assert.AreEqual("Player", PlayerNameSettings.DisplayName); Assert.AreEqual(3, PlayerNameSettings.Current.Revision);
        }
        [TestCase(null)] [TestCase("")] [TestCase("  ")] [TestCase("A\nB")] [TestCase("A\rB")] [TestCase("A\tB")]
        [TestCase("A\u200bB")] [TestCase("A\u2028B")] [TestCase("A\u202eB")]
        public void InvalidInputDoesNotSave(string value)
        { Assert.IsFalse(PlayerNameSettings.TrySave(value, out _)); Assert.IsFalse(PlayerPrefs.HasKey(key)); }
        [Test] public void CountsUnicodeScalarsAndRejectsOverflowWithoutTruncation()
        {
            string name = string.Concat(System.Linq.Enumerable.Repeat("\U0001f642", 24));
            Assert.IsTrue(PlayerNameSettings.TrySave(name, out _)); Assert.AreEqual(name, PlayerNameSettings.DisplayName);
            Assert.IsFalse(PlayerNameSettings.TrySave(name + "x", out _)); Assert.AreEqual(name, PlayerNameSettings.DisplayName);
            Assert.IsFalse(PlayerNameSettings.TrySave(new string('a', 25), out _));
        }
        private PortablePathSet Paths => PortablePathSet.Open(Path.Combine(root, "UserData"), Path.Combine(root, "UnityPlayer", "App_Data"));
        [Test] public void PortableMissingFieldRoundTripAndAvatarAreIndependent()
        {
            var paths = Paths; var file = new PortablePreferenceFile(paths);
            Assert.IsNull(file.PlayerProfile); Assert.IsTrue(file.SetPlayerAvatar("Avatars/Imported/example.vrm"));
            foreach (var name in new[] { "小羽", "91651", "老婆", "Player" })
            {
                Assert.IsTrue(file.SetPlayerProfile(new PlayerNamePreference(name, 5, 1234)));
                file = new PortablePreferenceFile(paths); Assert.IsNull(file.Error);
                Assert.AreEqual(name, file.PlayerProfile.DisplayName); Assert.AreEqual(5, file.PlayerProfile.Revision);
                Assert.AreEqual("Avatars/Imported/example.vrm", file.PlayerAvatar);
            }
            Assert.IsTrue(file.SetPlayerAvatar(null));
            Assert.AreEqual("Player", new PortablePreferenceFile(paths).PlayerProfile.DisplayName);
        }
        [Test] public void PortableFailedSaveRetainsProfileAndOldFile()
        {
            var paths = Paths; var file = new PortablePreferenceFile(paths);
            Assert.IsTrue(file.SetPlayerProfile(new PlayerNamePreference("小羽", 2, 100)));
            string before = File.ReadAllText(paths.SettingsFile);
            using (new FileStream(paths.SettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.IsFalse(file.SetPlayerProfile(new PlayerNamePreference("91651", 3, 200)));
            Assert.AreEqual("小羽", file.PlayerProfile.DisplayName); Assert.IsNotNull(file.Error);
            Assert.AreEqual(before, File.ReadAllText(paths.SettingsFile));
            Assert.AreEqual(0, Directory.GetFiles(paths.Root, "*.tmp").Length);
        }
        [Test] public void InvalidProfilePreservesSettingsAndReportsFailure()
        {
            var paths = Paths; var file = new PortablePreferenceFile(paths);
            var json = JObject.Parse(File.ReadAllText(paths.SettingsFile)); json["player_profile"] = new JObject { ["display_name"] = "", ["revision"] = 2 };
            string original = json.ToString(); File.WriteAllText(paths.SettingsFile, original);
            file = new PortablePreferenceFile(paths); Assert.IsNotNull(file.Error);
            Assert.IsFalse(file.SetPlayerProfile(new PlayerNamePreference("小羽", 3, 200)));
            Assert.AreEqual(original, File.ReadAllText(paths.SettingsFile));
        }
        [Test] public void UnpairedSurrogateIsRejected()
        { Assert.IsFalse(PlayerNameSettings.TrySave(new string((char)0xd800, 1), out _)); }
    }
}
