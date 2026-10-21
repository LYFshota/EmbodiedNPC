using System;
using System.IO;
using System.Linq;
using System.Reflection;
using CafeDemo;
using Newtonsoft.Json;
using NUnit.Framework;
using TMPro;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    public sealed class RuntimeTraceLiveOverlayTests
    {
        [Test]
        public void UiTimeUsesSystemZoneAcrossDatesWithoutChangingRawLine()
        {
            const string utc="2026-10-18T23:59:59.021Z";
            var expected=DateTimeOffset.Parse(utc,System.Globalization.CultureInfo.InvariantCulture).ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm:ss.fff zzz",System.Globalization.CultureInfo.InvariantCulture);
            Assert.AreEqual(expected,RuntimeTraceLiveBuffer.LocalTime(utc));
            Assert.AreEqual("—",RuntimeTraceLiveBuffer.LocalTime("not-a-timestamp"));
            Assert.AreEqual("—",RuntimeTraceLiveBuffer.LocalTime(""));
            var raw=Line(1).Replace("2026-09-29T12:01:14.021Z",utc);
            var buffer=new RuntimeTraceLiveBuffer();Assert.IsTrue(buffer.Add(raw));
            Assert.That(buffer.VisibleText("All"),Does.StartWith(expected));
            Assert.AreEqual(raw,buffer.LatestRawLine);
        }
        private static string Line(int seq, string category = "cognition", string kind = "cognitive_run_started") =>
            JsonConvert.SerializeObject(new { utc = "2026-09-29T12:01:14.021Z", category, kind,
                entity_id = "npc_01", run_id = "run_" + seq, data = new { status = "ok" } });

        [Test]
        public void BufferKeepsOnlyNewestThreeHundredLines()
        {
            var buffer = new RuntimeTraceLiveBuffer();
            for (int i = 0; i < RuntimeTraceLiveBuffer.Capacity + 2; i++) buffer.Add(Line(i));
            Assert.That(buffer.Count, Is.EqualTo(RuntimeTraceLiveBuffer.Capacity));
            Assert.That(buffer.VisibleText("All"), Does.Not.Contain("run_0 "));
            Assert.That(buffer.LatestRawLine, Is.EqualTo(Line(RuntimeTraceLiveBuffer.Capacity + 1)));
        }

        [Test]
        public void CategoryAndErrorsFiltersUseRealRecordedKinds()
        {
            var buffer = new RuntimeTraceLiveBuffer();
            buffer.Add(Line(1, "cognition"));
            buffer.Add(Line(2, "perception", "observation_applied"));
            buffer.Add(Line(3, "transport", "cognitive_transport_failure"));
            Assert.That(buffer.VisibleText("cognition"), Does.Contain("cognitive_run_started"));
            Assert.That(buffer.VisibleText("cognition"), Does.Not.Contain("observation_applied"));
            Assert.That(buffer.VisibleText("Errors"), Does.Contain("cognitive_transport_failure"));
            Assert.That(buffer.VisibleText("Errors"), Does.Not.Contain("cognitive_run_started"));
        }

        [Test]
        public void BadLineAndPausedViewDoNotEnterBuffer()
        {
            var buffer = new RuntimeTraceLiveBuffer();
            Assert.That(buffer.Add("{bad"), Is.False);
            buffer.Paused = true;
            Assert.That(buffer.Add(Line(1)), Is.False);
            Assert.That(buffer.Count, Is.Zero);
            buffer.Paused = false;
            Assert.That(buffer.Add(Line(2)), Is.True);
            Assert.That(buffer.Count, Is.EqualTo(1));
        }

        [Test]
        public void SceneHasFifthPageAndRecorderReference()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
            var panel = Object.FindAnyObjectByType<CafeDemoDeveloperPanel>();
            var overlay = Object.FindAnyObjectByType<CafeRuntimeTracePanel>();
            Assert.That(panel, Is.Not.Null);
            Assert.That(overlay, Is.Not.Null);
            Assert.That(panel.transform.parent.Find("DeveloperPanelRoot/Window/TracePage"), Is.Not.Null);
            Assert.That(panel.transform.parent.Find("DeveloperPanelRoot/Window/TraceNav"), Is.Not.Null);
            Assert.That(typeof(CafeRuntimeTracePanel).GetField("recorder", BindingFlags.Instance |
                BindingFlags.NonPublic).GetValue(overlay), Is.TypeOf<RuntimeTraceRecorder>());
            var root = panel.transform.parent.Find("DeveloperPanelRoot").gameObject;
            try
            {
                root.SetActive(true);
                panel.ShowTrace();
                var page = root.transform.Find("Window/TracePage");
                Assert.That(page.gameObject.activeSelf, Is.True);
                Assert.That(root.transform.Find("Window/DebugPage").gameObject.activeSelf, Is.False);
                overlay.Buffer.Add(Line(42));
                overlay.RefreshView();
                Assert.That(page.Find("TraceScrollView/Viewport/Content")
                    .GetComponent<TMP_Text>().text, Does.Contain("run_42"));
            }
            finally { root.SetActive(false); }
        }

        [Test]
        public void SuccessfulWriteSharesExactLineAndPauseClearNeverChangeJsonl()
        {
            string directory = Path.Combine(Path.GetTempPath(), "p7-live-" + Guid.NewGuid().ToString("N"));
            var owner = new GameObject("P7 Test Recorder");
            owner.SetActive(false);
            var recorder = owner.AddComponent<RuntimeTraceRecorder>();
            typeof(RuntimeTraceRecorder).GetField("outputDirectoryOverride",
                BindingFlags.Instance | BindingFlags.NonPublic).SetValue(recorder, directory);
            var buffer = new RuntimeTraceLiveBuffer();
            string delivered = null;
            recorder.TraceLineRecorded += _ => throw new InvalidOperationException("bad UI observer");
            recorder.TraceLineRecorded += line => { delivered = line; buffer.Add(line); };
            try
            {
                typeof(RuntimeTraceRecorder).GetMethod("StartRecording",
                    BindingFlags.Instance | BindingFlags.NonPublic).Invoke(recorder, null);
                string path = recorder.CurrentTracePath;
                Assert.That(File.Exists(path), Is.True);
                Assert.That(delivered, Is.EqualTo(ReadLinesWhileWriting(path).Last()));
                Assert.That(buffer.LatestRawLine, Is.EqualTo(delivered));
                int beforePause = buffer.Count;
                buffer.Paused = true;
                var record = typeof(RuntimeTraceRecorder).GetMethod("Record",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                record.Invoke(recorder, new object[] { "session", "p7_test_while_paused", null, null, null,
                    null, null, null, new { } });
                Assert.That(ReadLinesWhileWriting(path).Last(), Does.Contain("p7_test_while_paused"));
                Assert.That(buffer.Count, Is.EqualTo(beforePause));
                int fileLines = ReadLinesWhileWriting(path).Length;
                buffer.Clear();
                Assert.That(buffer.Count, Is.Zero);
                Assert.That(ReadLinesWhileWriting(path).Length, Is.EqualTo(fileLines));
            }
            finally
            {
                typeof(RuntimeTraceRecorder).GetMethod("StopRecording",
                    BindingFlags.Instance | BindingFlags.NonPublic).Invoke(recorder, null);
                Object.DestroyImmediate(owner);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Test]
        public void TraceTextSelectionCopiesButCannotEditRecordedContent()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
            var panel = Object.FindAnyObjectByType<CafeDemoDeveloperPanel>();
            var overlay = Object.FindAnyObjectByType<CafeRuntimeTracePanel>();
            var root = panel.transform.parent.Find("DeveloperPanelRoot").gameObject;
            string previousClipboard = GUIUtility.systemCopyBuffer;
            GameObject testEventSystem = null;
            try
            {
                if (UnityEngine.EventSystems.EventSystem.current == null)
                {
                    testEventSystem = new GameObject("TraceSelection_TestEventSystem");
                    var events = testEventSystem.AddComponent<UnityEngine.EventSystems.EventSystem>();
                    // EditMode does not dispatch EventSystem.OnEnable, which registers the current instance.
                    typeof(UnityEngine.EventSystems.EventSystem).GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(events, null);
                    UnityEngine.EventSystems.EventSystem.current = events;
                }
                root.SetActive(true);
                panel.ShowTrace();
                overlay.Buffer.Add(Line(42));
                overlay.RefreshView();
                var field = root.GetComponentInChildren<RuntimeTraceSelectionField>();
                Assert.That(field, Is.Not.Null);
                Assert.That(field.readOnly, Is.True);
                Assert.That(field.textComponent.raycastTarget, Is.True);
                string recorded = field.text;
                Canvas.ForceUpdateCanvases();
                field.textComponent.ForceMeshUpdate();
                var characters = field.textComponent.textInfo.characterInfo;
                var pointer = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current)
                {
                    button = UnityEngine.EventSystems.PointerEventData.InputButton.Left,
                    position = RectTransformUtility.WorldToScreenPoint(null, field.textComponent.transform.TransformPoint(
                        (characters[0].bottomLeft + characters[0].topRight) / 2))
                };
                field.OnPointerDown(pointer);
                field.OnBeginDrag(pointer);
                pointer.position = RectTransformUtility.WorldToScreenPoint(null, field.textComponent.transform.TransformPoint(
                    (characters[10].bottomLeft + characters[10].topRight) / 2));
                field.OnDrag(pointer);
                field.OnEndDrag(pointer);
                int begin = Math.Min(field.selectionStringAnchorPosition, field.selectionStringFocusPosition);
                int end = Math.Max(field.selectionStringAnchorPosition, field.selectionStringFocusPosition);
                Assert.That(end, Is.GreaterThan(begin), "Dragging across the rendered text must select characters.");
                field.ProcessEvent(Event.KeyboardEvent("^c"));
                Assert.That(GUIUtility.systemCopyBuffer, Is.EqualTo(recorded.Substring(begin, end - begin)));
                field.ProcessEvent(new Event { type = EventType.KeyDown, character = 'x', keyCode = KeyCode.X });
                Assert.That(field.text, Is.EqualTo(recorded));
                Assert.That(overlay.Buffer.VisibleText("All"), Is.EqualTo(recorded));
            }
            finally
            {
                GUIUtility.systemCopyBuffer = previousClipboard;
                if (testEventSystem != null) Object.DestroyImmediate(testEventSystem);
                root.SetActive(false);
                EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
            }
        }

        private static string[] ReadLinesWhileWriting(string path)
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(file);
            return reader.ReadToEnd().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r')).ToArray();
        }
    }
}
