using System;
using System.IO;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Settings;
using NUnit.Framework;

namespace CafeDemo.Tests
{
    public sealed class PortableUnityDataTests
    {
        private string temporary, root, app;
        private PortablePathSet paths;
        [SetUp] public void Setup()
        {
            temporary = Path.Combine(Path.GetTempPath(), "PortableUnity-" + Guid.NewGuid().ToString("N"));
            root = Path.Combine(temporary, "中文 package", "UserData");
            app = Path.Combine(temporary, "中文 package", "UnityPlayer", "EmbodiedNPC_Data");
            paths = PortablePathSet.Open(root, app);
            Assert.IsNull(paths.Error);
        }
        [TearDown] public void Cleanup()
        {
            Assert.IsTrue(temporary.StartsWith(Path.Combine(Path.GetTempPath(), "PortableUnity-"), StringComparison.Ordinal));
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
        }
        [Test] public void NewDefaultsAndPathsDoNotUseCwdOrLegacyFiles()
        {
            var settings = new PortablePreferenceFile(paths);
            Assert.IsNull(settings.Error); Assert.AreEqual(0, settings.Language); Assert.AreEqual(.5f, settings.BgmVolume);
            Assert.IsTrue(File.Exists(paths.SettingsFile)); Assert.IsFalse(File.Exists(paths.CharacterFile));
            Assert.AreEqual(Path.Combine(root, "runtime", "knowledge_admin_session.json"), paths.KnowledgeSessionFile);
            Assert.AreEqual(Path.Combine(root, "logs", "DebugTraces"), paths.TraceDirectory);
            Directory.CreateDirectory(Path.Combine(temporary, "Backend", "data"));
            File.WriteAllText(Path.Combine(temporary, "Backend", "data", "knowledge_admin_session.json"), "legacy");
            Assert.IsTrue(paths.TrySessionPath(null, out string session)); Assert.AreEqual(paths.KnowledgeSessionFile, session);
            Assert.IsFalse(File.Exists(session));
        }
        [Test] public void P3E_FreeCameraIsOptInAndSharesExistingPreferenceFile()
        {
            var settings=new PortablePreferenceFile(paths);Assert.IsFalse(settings.FreeCameraEnabled);
            settings.SetLanguage(true);settings.SetBgmVolume(.27f);settings.SetDisplayResolution("1920x1080");
            Assert.IsTrue(settings.SetFreeCameraEnabled(true));
            var reopened=new PortablePreferenceFile(paths);Assert.IsTrue(reopened.FreeCameraEnabled);
            Assert.AreEqual(1,reopened.Language);Assert.AreEqual(.27f,reopened.BgmVolume);Assert.AreEqual("1920x1080",reopened.DisplayResolution);
            Assert.IsTrue(reopened.SetFreeCameraEnabled(false));Assert.IsFalse(new PortablePreferenceFile(paths).FreeCameraEnabled);
        }
        [Test] public void ContextWarningDismissalPersistsWithoutChangingOtherPreferences()
        {
            var settings=new PortablePreferenceFile(paths);
            Assert.IsFalse(settings.ContextWarningDismissed);
            settings.SetLanguage(true);Assert.IsTrue(settings.DismissContextWarning());
            var reopened=new PortablePreferenceFile(paths);Assert.IsTrue(reopened.ContextWarningDismissed);
            reopened.SetLanguage(false);reopened.SetBgmVolume(.31f);Assert.IsTrue(reopened.Flush());
            var again=new PortablePreferenceFile(paths);Assert.IsTrue(again.ContextWarningDismissed);
            Assert.AreEqual(0,again.Language);Assert.AreEqual(.31f,again.BgmVolume);
        }
        [TestCase(3840,2400,0)] [TestCase(3840,2160,1)]
        [TestCase(2880,1800,1)] [TestCase(2560,1600,1)]
        [TestCase(2560,1440,2)] [TestCase(1920,1200,2)]
        [TestCase(1920,1080,3)] [TestCase(1366,768,3)]
        public void P3D_DefaultResolutionUsesBothDimensions(int width,int height,int expected)
        { Assert.AreEqual(expected,DisplaySettings.DefaultIndex(width,height)); }
        [Test] public void P3D_DisplayAndExportPersistWithoutBackendOrPriorExport()
        {
            var settings=new PortablePreferenceFile(paths);settings.SetLanguage(true);settings.SetBgmVolume(.27f);
            Assert.IsTrue(settings.SetDisplayResolution("2560x1440"));
            var reopened=new PortablePreferenceFile(paths);
            Assert.AreEqual("2560x1440",reopened.DisplayResolution);Assert.AreEqual(1,reopened.Language);Assert.AreEqual(.27f,reopened.BgmVolume);
            Assert.IsFalse(reopened.SetDisplayResolution("8192x4096"));
            var first=DiagnosticsExport.EnsureExportDirectory(root);Assert.IsTrue(Directory.Exists(first));
            Assert.AreEqual(first,DiagnosticsExport.EnsureExportDirectory(root));Assert.IsEmpty(Directory.GetFiles(first));
            foreach(float dpi in new[]{96f,144f,192f})
            {
                var fitted=DisplaySettings.FitWindow(new UnityEngine.Vector2Int(1280,720),new UnityEngine.Vector2Int(1024,700),dpi);
                Assert.Less(fitted.x,1024);Assert.Less(fitted.y,700);
                Assert.Greater(fitted.x,0);Assert.Greater(fitted.y,0);
            }
        }
        [TestCase(null, "PORTABLE_ROOT_MISSING")]
        [TestCase("", "PORTABLE_ROOT_MISSING")]
        [TestCase("UserData", "PORTABLE_ROOT_RELATIVE")]
        [TestCase("C:UserData", "PORTABLE_ROOT_RELATIVE")]
        public void MissingAndRelativeRootsNeverFallback(string value, string error)
        { Assert.AreEqual(error, PortablePathSet.Open(value, app).Error); }
        [Test] public void ProgramAndDevelopmentDirectoriesRejected()
        {
            Assert.AreEqual("PORTABLE_ROOT_UNSAFE", PortablePathSet.Open(Path.GetDirectoryName(app), app).Error);
            Assert.AreEqual("PORTABLE_ROOT_UNSAFE", PortablePathSet.Open(Path.Combine(temporary, "中文 package", "Backend", "data"), app).Error);
            string source = Path.Combine(temporary, "source"); Directory.CreateDirectory(Path.Combine(source, "AIBackend", "app"));
            Assert.AreEqual("PORTABLE_ROOT_UNSAFE", PortablePathSet.Open(Path.Combine(source, "AIBackend", "data"), app).Error);
        }
        [Test] public void UnwritableRootFailsWithoutFallback()
        {
            string blocked = Path.Combine(temporary, "blocked"); File.WriteAllText(blocked, "keep");
            Assert.AreEqual("PORTABLE_ROOT_UNWRITABLE", PortablePathSet.Open(Path.Combine(blocked, "UserData"), app).Error);
            Assert.AreEqual("keep", File.ReadAllText(blocked));
        }
        [Test] public void SavesBothPreferencesAtomicallyAndPreservesExistingOnLoad()
        {
            var settings = new PortablePreferenceFile(paths); settings.SetBgmVolume(.27f); settings.SetLanguage(true);
            byte[] saved = File.ReadAllBytes(paths.SettingsFile); var time = File.GetLastWriteTimeUtc(paths.SettingsFile);
            var reopened = new PortablePreferenceFile(paths); Assert.IsNull(reopened.Error);
            Assert.AreEqual(1, reopened.Language); Assert.AreEqual(.27f, reopened.BgmVolume);
            CollectionAssert.AreEqual(saved, File.ReadAllBytes(paths.SettingsFile)); Assert.AreEqual(time, File.GetLastWriteTimeUtc(paths.SettingsFile));
            Assert.AreEqual(0, Directory.GetFiles(root, "*.tmp").Length);
        }
        [TestCase("broken-json")]
        [TestCase("{\"schema_version\":2,\"language\":0,\"bgm_volume\":0.5}")]
        [TestCase("{\"schema_version\":1,\"language\":0,\"bgm_volume\":2}")]
        [TestCase("{\"schema_version\":1,\"language\":0,\"language\":1,\"bgm_volume\":0.5}")]
        public void InvalidPreferencesPreservedAndWritesRejected(string document)
        {
            File.WriteAllText(paths.SettingsFile, document); var settings = new PortablePreferenceFile(paths);
            Assert.AreEqual("PORTABLE_SETTINGS_INVALID", settings.Error); settings.SetLanguage(true); settings.SetBgmVolume(.2f);
            Assert.IsFalse(settings.Flush()); Assert.AreEqual(document, File.ReadAllText(paths.SettingsFile));
        }
        [Test] public void LockedSettingsAndFailedAtomicSavePreservePreviousFile()
        {
            var settings = new PortablePreferenceFile(paths); string before = File.ReadAllText(paths.SettingsFile);
            using (new FileStream(paths.SettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.AreEqual("PORTABLE_SETTINGS_UNREADABLE", new PortablePreferenceFile(paths).Error);
                settings.SetBgmVolume(.2f); Assert.IsFalse(settings.Flush()); Assert.AreEqual("PORTABLE_SETTINGS_SAVE_FAILED", settings.Error);
            }
            Assert.AreEqual(before, File.ReadAllText(paths.SettingsFile));
        }
        [Test] public void ExplicitSessionPathRemainsAuthoritativeAndContained()
        {
            Assert.IsTrue(paths.TrySessionPath(Path.Combine(root, "runtime", "missing.json"), out string file));
            Assert.AreEqual(Path.Combine(root, "runtime", "missing.json"), file);
            Assert.IsFalse(paths.TrySessionPath("relative.json", out _));
            Assert.IsFalse(paths.TrySessionPath(Path.Combine(root, "..", "Backend", "data", "session.json"), out _));
            Assert.IsFalse(paths.TrySessionPath("", out _));
        }
        [Test] public void PersonaAllFieldsPersistAtPortablePathWithoutCopyingDefaults()
        {
            var store = new CharacterOverrideStore(paths.CharacterFile); Assert.IsFalse(File.Exists(paths.CharacterFile));
            store.Save("portable-test", "测试名字", "简介", "背景", "性格", "说话风格", "行为边界");
            var reopened = new CharacterOverrideStore(paths.CharacterFile); Assert.IsTrue(reopened.TryGet("portable-test", out var value));
            Assert.AreEqual("测试名字", value.display_name); Assert.AreEqual("简介", value.role_summary); Assert.AreEqual("背景", value.backstory);
            Assert.AreEqual("性格", value.personality); Assert.AreEqual("说话风格", value.speaking_style); Assert.AreEqual("行为边界", value.boundaries);
            reopened.Remove("portable-test"); Assert.IsFalse(new CharacterOverrideStore(paths.CharacterFile).TryGet("portable-test", out _));
        }
        [Test] public void PortableFailureIsNotReportedAsProviderOrHttpFailure()
        {
            Assert.AreEqual(StartupUiFailure.Portable, StartupProgressDisplay.ConfigFailure(null, "PORTABLE_ROOT_MISSING"));
            Assert.AreEqual(StartupUiFailure.Portable, StartupProgressDisplay.ConfigFailure(null, "PORTABLE_SETTINGS_INVALID"));
        }
    }
}
