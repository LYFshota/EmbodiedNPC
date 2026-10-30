using System;
using System.IO;
using EmbodiedNPC.Settings;
using NUnit.Framework;
using Newtonsoft.Json.Linq;

namespace CafeDemo.Tests
{
    public sealed class PlayerAppearanceSettingsTests
    {
        private string root;
        private PortablePathSet paths;
        [SetUp] public void Setup()
        {
            root = Path.Combine(Path.GetTempPath(), "AppearanceMvp-" + Guid.NewGuid().ToString("N"));
            paths = PortablePathSet.Open(root, Path.Combine(root, "Game", "App_Data"));
        }
        [TearDown] public void Cleanup()
        { if (root.StartsWith(Path.Combine(Path.GetTempPath(), "AppearanceMvp-"), StringComparison.Ordinal) && Directory.Exists(root)) Directory.Delete(root, true); }
        [Test] public void OptionalDescriptionRestoresAndSwitchingModelsClearsItWithoutChangingProfile()
        {
            var file = new PortablePreferenceFile(paths); var store = new PlayerAvatarStore(paths,file);
            Assert.IsNull(store.AppearanceDescription);
            var profile = new PlayerNamePreference("小羽",2,100); Assert.IsTrue(file.SetPlayerProfile(profile));
            var first = "Avatars/Imported/" + new string('a',64) + ".vrm";
            var second = "Avatars/Imported/" + new string('b',64) + ".vrm";
            Assert.IsTrue(store.Select(first)); Assert.IsTrue(store.SetAppearanceDescription("银白色长发。"));
            var restored = new PortablePreferenceFile(paths); Assert.AreEqual("银白色长发。",restored.PlayerAppearanceDescription);
            Assert.AreEqual(first, restored.PlayerAvatar); Assert.AreEqual("小羽",restored.PlayerProfile.DisplayName);
            Assert.IsTrue(store.Select(second)); Assert.IsNull(store.AppearanceDescription);
            Assert.IsTrue(store.Select(first)); Assert.IsNull(store.AppearanceDescription);
            Assert.IsTrue(store.SetAppearanceDescription("黑色外套。")); Assert.IsTrue(store.Select(null));
            Assert.IsNull(store.AppearanceDescription); Assert.AreEqual("小羽",new PortablePreferenceFile(paths).PlayerProfile.DisplayName);
        }
        [Test] public void BoundedTextClearAndFailedSaveKeepPreviousFile()
        {
            var file = new PortablePreferenceFile(paths);
            Assert.IsTrue(file.SetPlayerAppearanceDescription("银白色长发。\r\n浅蓝色外套。"));
            Assert.AreEqual("银白色长发。\n浅蓝色外套。",new PortablePreferenceFile(paths).PlayerAppearanceDescription);
            Assert.IsTrue(file.SetPlayerAppearanceDescription(new string('发',160)));
            Assert.IsFalse(file.SetPlayerAppearanceDescription(new string('发',161)));
            foreach (var text in new[]{"A\tB","A\u200bB",new string((char)0xd800,1)})
                Assert.IsFalse(file.SetPlayerAppearanceDescription(text));
            string before=File.ReadAllText(paths.SettingsFile);
            using(new FileStream(paths.SettingsFile,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
                Assert.IsFalse(file.SetPlayerAppearanceDescription("外套。"));
            Assert.AreEqual(new string('发',160),file.PlayerAppearanceDescription); Assert.AreEqual(before,File.ReadAllText(paths.SettingsFile));
            file=new PortablePreferenceFile(paths); Assert.IsTrue(file.SetPlayerAppearanceDescription(" "));
            Assert.IsNull(new PortablePreferenceFile(paths).PlayerAppearanceDescription);
        }
        [Test] public void ManualAppearanceIsNotAutomaticallyPersistedAsWitnessedMemory()
        {
            var fields=new System.Collections.Generic.Dictionary<string,string>{["entity_kind"]="Agent",["agent_role"]="Player",
                ["visibility_state"]="current_visible",["appearance_description"]="银白色长发。"};
            var observation=new EmbodiedNPC.Perception.Observation("appearance_obs","npc_01",EmbodiedNPC.Perception.ObservationSourceType.ContinuousScan,
                "visual:player_01",EmbodiedNPC.Perception.ObservationChannel.Visual,"player_01",fields,Array.Empty<string>(),1,0,1,10);
            var draft=EmbodiedNPC.Memory.ObservationExperienceAdapter.Create(observation);
            var known=(System.Collections.Generic.Dictionary<string,object>)draft.StructuredPayload["known_fields"];
            Assert.IsFalse(known.ContainsKey("appearance_description")); Assert.AreEqual("current_visible",known["visibility_state"]);
            Assert.AreEqual("银白色长发。",observation.KnownFields["appearance_description"]);
        }
        [Test] public void WrongModelDescriptionIsUnknownWithoutBreakingOtherSettings()
        {
            var file = new PortablePreferenceFile(paths); file.SetPlayerProfile(new PlayerNamePreference("小羽",2,100));
            var doc=JObject.Parse(File.ReadAllText(paths.SettingsFile));
            doc["player_appearance"]=new JObject{["avatar"]="different-model",["description"]="红色长裙。"};
            File.WriteAllText(paths.SettingsFile,doc.ToString()); file=new PortablePreferenceFile(paths);
            Assert.IsNull(file.Error); Assert.IsNull(file.PlayerAppearanceDescription); Assert.AreEqual("小羽",file.PlayerProfile.DisplayName);
        }
    }
}
