using System;
using System.IO;
using System.Text.RegularExpressions;
using EmbodiedNPC.Cognition;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace EmbodiedNPC.Tests
{
    public sealed class CharacterSettingsTests
    {
        private string directory;
        private string path;
        private CharacterDefinition definition;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "embodiednpc-character-" + Guid.NewGuid().ToString("N"));
            path = Path.Combine(directory, CharacterOverrideStore.FileName);
            definition = ScriptableObject.CreateInstance<CharacterDefinition>();
            definition.Configure("cafe.merchant", "店员", "咖啡馆店员，负责接待顾客", "在咖啡馆工作几年。", "温和慢热。", "友好、自然、简洁", "不编造共同经历。");
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(definition);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        [Test]
        public void MissingFile_UsesBuiltInCharacterWithoutCreatingAUserCopy()
        {
            var store = new CharacterOverrideStore(path);
            CognitiveCharacterDto effective = CharacterProfileResolver.Resolve(definition, store);
            Assert.That(effective.character_id, Is.EqualTo(definition.CharacterId));
            Assert.That(effective.display_name, Is.EqualTo(definition.DisplayName));
            Assert.That(effective.role_summary, Is.EqualTo(definition.RoleSummary));
            Assert.That(effective.speaking_style, Is.EqualTo(definition.SpeakingStyle));
            Assert.That(File.Exists(path), Is.False);
        }

        [Test]
        public void SaveOverride_PersistsSixFieldsWhileAuthoringIdentityStaysFixed()
        {
            var store = new CharacterOverrideStore(path);
            store.Save(definition.CharacterId, "阿明", "会认真服务顾客的店员", "学过咖啡制作。", "有点幽默。", "有点毒舌，但回答简短。", "不知道就承认。");
            var reloaded = new CharacterOverrideStore(path);
            CognitiveCharacterDto effective = CharacterProfileResolver.Resolve(definition, reloaded);
            Assert.That(effective.character_id, Is.EqualTo("cafe.merchant"));
            Assert.That(effective.display_name, Is.EqualTo("阿明"));
            Assert.That(effective.role_summary, Is.EqualTo("会认真服务顾客的店员"));
            Assert.That(effective.speaking_style, Is.EqualTo("有点毒舌，但回答简短。"));
            Assert.That(definition.DisplayName, Is.EqualTo("店员"));
            Assert.That(effective.backstory, Is.EqualTo("学过咖啡制作。"));
            Assert.That(effective.personality, Is.EqualTo("有点幽默。"));
            Assert.That(effective.boundaries, Is.EqualTo("不知道就承认。"));
            Assert.That(File.ReadAllText(path), Does.Contain("\"schema_version\": 2"));
        }

        [Test]
        public void RestoreDefault_RemovesOnlyThisCharactersOverride()
        {
            var store = new CharacterOverrideStore(path);
            store.Save(definition.CharacterId, "阿明", "新身份", "背景", "性格", "新风格", "边界");
            store.Save("cafe.regular_customer", "顾客甲", "普通顾客", "背景", "性格", "自然", "边界");
            store.Remove(definition.CharacterId);
            Assert.That(CharacterProfileResolver.Resolve(definition, store).display_name, Is.EqualTo("店员"));
            var reloaded = new CharacterOverrideStore(path);
            Assert.That(reloaded.TryGet(definition.CharacterId, out _), Is.False);
            Assert.That(reloaded.TryGet("cafe.regular_customer", out _), Is.True);
        }

        [Test]
        public void CorruptFile_FallsBackToDefaultsWithoutRewritingIt()
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "{bad json");
            LogAssert.Expect(LogType.Warning, new Regex("NPC character overrides could not be loaded"));
            var store = new CharacterOverrideStore(path);
            Assert.That(CharacterProfileResolver.Resolve(definition, store).display_name, Is.EqualTo("店员"));
            Assert.That(File.ReadAllText(path), Is.EqualTo("{bad json"));
            Assert.Throws<IOException>(() => store.Save(definition.CharacterId, "姓名", "身份", "背景", "性格", "风格", "边界"));
            Assert.That(File.ReadAllText(path), Is.EqualTo("{bad json"));
        }

        [Test]
        public void CorruptFile_RestoreMustReportFailureAndPreserveFile()
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "{bad json");
            LogAssert.Expect(LogType.Warning, new Regex("NPC character overrides could not be loaded"));
            var store = new CharacterOverrideStore(path);
            Assert.Throws<IOException>(() => store.Remove(definition.CharacterId));
            Assert.That(File.ReadAllText(path), Is.EqualTo("{bad json"));
        }

        [Test]
        public void V1_LoadUsesRealDefaults_ThenSaveUpgradesAllEntriesWithoutLosingOldValues()
        {
            Directory.CreateDirectory(directory);
            string original = "{\"schema_version\":1,\"characters\":[{\"character_id\":\"cafe.merchant\",\"display_name\":\"旧店员\",\"role_summary\":\"旧身份\",\"speaking_style\":\"旧风格\"},{\"character_id\":\"cafe.regular_customer\",\"display_name\":\"旧顾客\",\"role_summary\":\"旧顾客身份\",\"speaking_style\":\"旧顾客风格\"}]}";
            File.WriteAllText(path, original);
            var customer = ScriptableObject.CreateInstance<CharacterDefinition>();
            try
            {
                customer.Configure("cafe.regular_customer", "顾客", "顾客身份", "顾客背景", "顾客性格", "顾客风格", "顾客边界");
                var store = new CharacterOverrideStore(path);
                var effective = CharacterProfileResolver.Resolve(definition, store);
                CharacterProfileResolver.Resolve(customer, store);
                Assert.That(effective.display_name, Is.EqualTo("旧店员"));
                Assert.That(effective.role_summary, Is.EqualTo("旧身份"));
                Assert.That(effective.speaking_style, Is.EqualTo("旧风格"));
                Assert.That(effective.backstory, Is.EqualTo(definition.Backstory));
                Assert.That(effective.personality, Is.EqualTo(definition.Personality));
                Assert.That(effective.boundaries, Is.EqualTo(definition.Boundaries));
                Assert.That(File.ReadAllText(path), Is.EqualTo(original), "Loading must not upgrade the file.");
                store.Save(definition.CharacterId, effective.display_name, effective.role_summary,
                    effective.backstory, effective.personality, effective.speaking_style, effective.boundaries);
                var reloaded = new CharacterOverrideStore(path);
                var other = CharacterProfileResolver.Resolve(customer, reloaded);
                Assert.That(other.IsValid, Is.True);
                Assert.That(other.display_name, Is.EqualTo("旧顾客"));
                Assert.That(other.role_summary, Is.EqualTo("旧顾客身份"));
                Assert.That(other.speaking_style, Is.EqualTo("旧顾客风格"));
                Assert.That(other.backstory, Is.EqualTo(customer.Backstory));
                Assert.That(other.personality, Is.EqualTo(customer.Personality));
                Assert.That(other.boundaries, Is.EqualTo(customer.Boundaries));
                Assert.That(File.ReadAllText(path), Does.Contain("\"schema_version\": 2"));
            }
            finally { UnityEngine.Object.DestroyImmediate(customer); }
        }

        [TestCase(3)]
        [TestCase(2)]
        public void FutureOrIncompleteV2_FallsBackAndPreservesRejectedFile(int schema)
        {
            Directory.CreateDirectory(directory);
            string original = "{\"schema_version\":" + schema + ",\"characters\":[{\"character_id\":\"cafe.merchant\",\"display_name\":\"旧店员\",\"role_summary\":\"身份\",\"speaking_style\":\"风格\"}]}";
            File.WriteAllText(path, original);
            LogAssert.Expect(LogType.Warning, new Regex("NPC character overrides could not be loaded"));
            var store = new CharacterOverrideStore(path);
            Assert.That(CharacterProfileResolver.Resolve(definition, store).display_name, Is.EqualTo(definition.DisplayName));
            Assert.Throws<IOException>(() => store.Save(definition.CharacterId, "姓名", "身份", "背景", "性格", "风格", "边界"));
            Assert.That(File.ReadAllText(path), Is.EqualTo(original));
        }

        [Test]
        public void Backstory1200_RoundTripsUnicode_OverflowPreservesSavedFile()
        {
            var store = new CharacterOverrideStore(path);
            string legal = new string('人', 1199) + "🙂";
            Assert.That(CharacterDefinition.CountCharacters(legal), Is.EqualTo(1200));
            store.Save(definition.CharacterId, definition.DisplayName, definition.RoleSummary, legal,
                definition.Personality, definition.SpeakingStyle, definition.Boundaries);
            string saved = File.ReadAllText(path);
            Assert.That(CharacterProfileResolver.Resolve(definition, new CharacterOverrideStore(path)).backstory,
                Is.EqualTo(legal));
            Assert.Throws<ArgumentException>(() => store.Save(definition.CharacterId, definition.DisplayName,
                definition.RoleSummary, legal + "人", definition.Personality, definition.SpeakingStyle, definition.Boundaries));
            Assert.That(File.ReadAllText(path), Is.EqualTo(saved));
        }

        [TestCase("display_name", 80)]
        [TestCase("role_summary", 240)]
        [TestCase("backstory", 1200)]
        [TestCase("personality", 600)]
        [TestCase("speaking_style", 480)]
        [TestCase("boundaries", 600)]
        public void AllLayersRejectBlankAndOversizedFieldsWithoutSaving(string field, int limit)
        {
            var dto = CognitiveCharacterDto.From(definition);
            var member = typeof(CognitiveCharacterDto).GetField(field);
            var store = new CharacterOverrideStore(path);
            foreach (string invalid in new[] { "  ", new string('人', limit + 1) })
            {
                member.SetValue(dto, invalid);
                Assert.That(dto.IsValid, Is.False);
                Assert.Throws<ArgumentException>(() => store.Save(dto.character_id, dto.display_name, dto.role_summary,
                    dto.backstory, dto.personality, dto.speaking_style, dto.boundaries));
                definition.Configure(dto.character_id, dto.display_name, dto.role_summary, dto.backstory,
                    dto.personality, dto.speaking_style, dto.boundaries);
                Assert.That(definition.TryValidateConfiguration(out _), Is.False);
                Assert.That(File.Exists(path), Is.False);
            }
            member.SetValue(dto, new string('人', limit));
            Assert.That(dto.IsValid, Is.True);
        }
    }
}
