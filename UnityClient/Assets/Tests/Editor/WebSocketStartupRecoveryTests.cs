using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Input;
using TMPro;
using UnityEngine.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace EmbodiedNPC.Tests
{
    public sealed class WebSocketStartupRecoveryTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject root;
        private BackendWebSocketClient backend;
        private SocketPeer peer;
        private Task ConnectionTask => (Task)Get("connectionTask");
        private object Get(string field) => typeof(BackendWebSocketClient).GetField(field, Private).GetValue(backend);
        private void Set(string field, object value) => typeof(BackendWebSocketClient).GetField(field, Private).SetValue(backend, value);

        [SetUp]
        public void SetUp()
        {
            root = new GameObject("WebSocketStartupRecoveryTest");
            backend = root.AddComponent<BackendWebSocketClient>();
            Assert.That(Get("maxConnectAttempts"), Is.EqualTo(3));
            Assert.That(Get("retryDelaySeconds"), Is.EqualTo(1f));
            Assert.That(Get("connectTimeoutSeconds"), Is.EqualTo(5f));
            Set("connectOnStart", false);
            // Real loop and sockets, shorter waits only in this isolated fixture.
            Set("retryDelaySeconds", .02f);
            Set("connectTimeoutSeconds", .3f);
            peer = new SocketPeer();
            Set("serverUrl", "ws://127.0.0.1:" + peer.Port + "/ws");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Task disconnect = backend.DisconnectAsync();
            peer.Dispose();
            yield return Until(() => disconnect.IsCompleted);
            Assert.That(disconnect.IsFaulted, Is.False, disconnect.Exception?.ToString());
            Object.DestroyImmediate(root);
        }

        private static IEnumerator Until(Func<bool> condition)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 5;
            while (!condition() && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.That(condition(), Is.True, "Transport did not reach the expected bounded state.");
        }

        [Test]
        public void ActiveTask_RepeatedEnsureAndContextMenuKeepTheSameOwner()
        {
            var running = new TaskCompletionSource<bool>();
            var lease = new CancellationTokenSource();
            Set("connectionTask", running.Task);
            Set("lifetime", lease);
            Assert.That(backend.EnsureConnected(), Is.True);
            backend.EnsureConnected(); backend.ConnectFromContextMenu();
            Assert.That(ConnectionTask, Is.SameAs(running.Task));
            Assert.That(Get("lifetime"), Is.SameAs(lease));
            Assert.That(peer.Accepted, Is.Zero);
            Assert.That(lease.IsCancellationRequested, Is.False);
            running.SetResult(true);
        }

        [UnityTest]
        public IEnumerator AlreadyConnected_NoSocketAttemptResetOrDuplicateConnected()
        {
            int connected = 0;
            backend.ConnectionStatusChanged += status => { if (status == "CONNECTED") connected++; };
            Assert.That(backend.EnsureConnected(), Is.True);
            yield return Until(() => backend.ConnectionStatus == "CONNECTED" && backend.IsConnected);
            Task task = ConnectionTask;
            object lease = Get("lifetime");
            backend.EnsureConnected(); backend.ConnectFromContextMenu(); backend.EnsureConnected();
            yield return null;
            Assert.That(ConnectionTask, Is.SameAs(task));
            Assert.That(Get("lifetime"), Is.SameAs(lease));
            Assert.That(backend.ConnectionAttempts, Is.EqualTo(1));
            Assert.That(peer.Accepted, Is.EqualTo(1));
            Assert.That(connected, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ExhaustedRound_RearmsSameBoundedLoopAndDisposesCompletedLease()
        {
            peer.RejectRemaining = 3;
            backend.EnsureConnected();
            yield return Until(() => ConnectionTask.IsCompleted);
            Assert.That(backend.ConnectionAttempts, Is.EqualTo(3));
            Assert.That(backend.ConnectionStatus, Is.EqualTo("OFFLINE"));
            var oldLease = (CancellationTokenSource)Get("lifetime");
            Task oldTask = ConnectionTask;
            int connected = 0;
            backend.ConnectionStatusChanged += status => { if (status == "CONNECTED") connected++; };
            backend.ConnectFromContextMenu(); backend.EnsureConnected(); backend.EnsureConnected();
            Assert.That(ConnectionTask, Is.Not.SameAs(oldTask));
            Assert.Throws<ObjectDisposedException>(() => { var ignored = oldLease.Token; });
            yield return Until(() => backend.ConnectionStatus == "CONNECTED" && backend.IsConnected);
            Assert.That(backend.ConnectionAttempts, Is.EqualTo(1));
            Assert.That(peer.Accepted, Is.EqualTo(4));
            Assert.That(connected, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator RecoveryRoundAlsoExhaustsAtThreeAndDoesNotPoll()
        {
            peer.RejectRemaining = 6;
            backend.EnsureConnected();
            yield return Until(() => ConnectionTask.IsCompleted);
            backend.EnsureConnected();
            yield return Until(() => ConnectionTask.IsCompleted);
            Task finished = ConnectionTask;
            Assert.That(backend.ConnectionAttempts, Is.EqualTo(3));
            Assert.That(peer.Accepted, Is.EqualTo(6));
            double stableUntil = Time.realtimeSinceStartupAsDouble + .2;
            while (Time.realtimeSinceStartupAsDouble < stableUntil) yield return null;
            Assert.That(ConnectionTask, Is.SameAs(finished));
            Assert.That(peer.Accepted, Is.EqualTo(6));
            Assert.That(backend.ConnectionStatus, Is.EqualTo("OFFLINE"));
        }

        [UnityTest]
        public IEnumerator ConnectingObserver_ReentrantEnsureCannotCreateAnotherLoop()
        {
            backend.ConnectionStatusChanged += status =>
            {
                if (status == "CONNECTING") { backend.EnsureConnected(); backend.ConnectFromContextMenu(); }
            };
            backend.EnsureConnected();
            yield return Until(() => backend.ConnectionStatus == "CONNECTED");
            Assert.That(peer.Accepted, Is.EqualTo(1));
            Assert.That(backend.ConnectionAttempts, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator DisableDuringSecondRound_CancelsSocketAndRejectsLateConnected()
        {
            peer.RejectRemaining = 3;
            backend.EnsureConnected();
            yield return Until(() => ConnectionTask.IsCompleted);
            peer.HandshakeGate.Reset();
            int connected = 0;
            backend.ConnectionStatusChanged += status => { if (status == "CONNECTED") connected++; };
            backend.EnsureConnected();
            yield return Until(() => peer.Accepted == 4);
            Task second = ConnectionTask;
            backend.enabled = false;
            // EditMode does not invoke MonoBehaviour lifecycle automatically.
            typeof(BackendWebSocketClient).GetMethod("OnDisable", Private).Invoke(backend, null);
            Assert.That(backend.EnsureConnected(), Is.False);
            peer.HandshakeGate.Set();
            yield return Until(() => second.IsCompleted && backend.ConnectionStatus == "OFFLINE");
            Assert.That(backend.IsConnected, Is.False);
            Assert.That(Get("websocket"), Is.Null);
            Assert.That(Get("lifetime"), Is.Null);
            Assert.That(connected, Is.Zero);
        }

        [UnityTest]
        public IEnumerator ConnectingObserver_DisableDoesNotDisposeAnInUseLease()
        {
            backend.ConnectionStatusChanged += status =>
            {
                if (status != "CONNECTING") return;
                var lease = (CancellationTokenSource)Get("lifetime");
                backend.enabled = false;
                typeof(BackendWebSocketClient).GetMethod("OnDisable", Private).Invoke(backend, null);
                Assert.That(lease.IsCancellationRequested, Is.True);
                Assert.DoesNotThrow(() => { var ignored = lease.Token; }, "The loop still owns this lease until its task completes.");
            };
            backend.EnsureConnected();
            yield return Until(() => ConnectionTask.IsCompleted && backend.ConnectionStatus == "OFFLINE");
            Assert.That(backend.IsConnected, Is.False);
            Assert.That(Get("lifetime"), Is.Null);
        }

        private DialogueController CreateOpenDialogue(out TextUtteranceInput input, out Button button)
        {
            var inputObject = new GameObject("RecoveryInput", typeof(RectTransform), typeof(CanvasGroup));
            inputObject.transform.SetParent(root.transform);
            input = inputObject.AddComponent<TextUtteranceInput>();
            var field = inputObject.AddComponent<TMP_InputField>();
            var buttonObject = new GameObject("Send", typeof(RectTransform), typeof(Button));
            buttonObject.transform.SetParent(inputObject.transform);
            button = buttonObject.GetComponent<Button>();
            typeof(TextUtteranceInput).GetField("inputField", Private).SetValue(input, field);
            typeof(TextUtteranceInput).GetField("submitButton", Private).SetValue(input, button);
            var context = new DialogueContext { conversationId = "late-backend", speakerEntityId = "player", targetAgentId = "npc" };
            input.BeginInput(context);
            input.ShowInput();
            field.text = "keep this draft";
            input.SetSubmissionReady(false);
            var controller = root.AddComponent<DialogueController>();
            typeof(DialogueController).GetField("backendClient", Private).SetValue(controller, backend);
            typeof(DialogueController).GetField("textInput", Private).SetValue(controller, input);
            typeof(DialogueController).GetField("activeContext", Private).SetValue(controller, context);
            typeof(DialogueController).GetField("presentationVisible", Private).SetValue(controller, true);
            return controller;
        }

        private static void RefreshDialogue(DialogueController controller) =>
            typeof(DialogueController).GetMethod("RefreshTransportReadiness", Private).Invoke(controller, null);

        [UnityTest]
        public IEnumerator OpenDialogue_AfterStartupExhaustion_RecoversSendAndPreservesDraft()
        {
            peer.RejectRemaining = 3;
            backend.EnsureConnected();
            yield return Until(() => ConnectionTask.IsCompleted);
            Assert.That(backend.ConnectionStatus, Is.EqualTo("OFFLINE"));
            var controller = CreateOpenDialogue(out var input, out var button);
            Assert.That(button.interactable, Is.False);
            yield return Until(() =>
            {
                RefreshDialogue(controller);
                return backend.IsConnected && input.IsSubmissionReady && button.interactable;
            });
            Assert.That(peer.Accepted, Is.EqualTo(4));
            Assert.That(backend.ConnectionAttempts, Is.EqualTo(1));
            Assert.That(input.CurrentText, Is.EqualTo("keep this draft"));
            var context = (DialogueContext)typeof(DialogueController).GetField("activeContext", Private).GetValue(controller);
            Assert.That(context.conversationId, Is.EqualTo("late-backend"));
        }

        [UnityTest]
        public IEnumerator HiddenOrUnconfiguredDialogue_DoesNotRearmExhaustedTransport()
        {
            peer.RejectRemaining = 3;
            backend.EnsureConnected();
            yield return Until(() => ConnectionTask.IsCompleted);
            var finished = ConnectionTask;
            var controller = CreateOpenDialogue(out var input, out var button);
            typeof(DialogueController).GetField("presentationVisible", Private).SetValue(controller, false);
            RefreshDialogue(controller);
            yield return null;
            Assert.That(ConnectionTask, Is.SameAs(finished));
            typeof(DialogueController).GetField("presentationVisible", Private).SetValue(controller, true);
            typeof(DialogueController).GetField("setupReady", Private).SetValue(controller, false);
            RefreshDialogue(controller);
            yield return null;
            Assert.That(ConnectionTask, Is.SameAs(finished));
            Assert.That(peer.Accepted, Is.EqualTo(3));
            Assert.That(button.interactable, Is.False);
            Assert.That(input.CurrentText, Is.EqualTo("keep this draft"));
        }

        // Mono HttpListener WebSocket upgrade is unsupported; exercise the real client over TCP.
        private sealed class SocketPeer : IDisposable
        {
            private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly ManualResetEventSlim release = new ManualResetEventSlim(false);
            private TcpClient current;
            private volatile bool disposed;
            public readonly ManualResetEventSlim HandshakeGate = new ManualResetEventSlim(true);
            public int Accepted;
            public int RejectRemaining;
            public int Port { get; }

            public SocketPeer()
            {
                listener.Start();
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Task.Run(() => Serve());
            }

            private void Serve()
            {
                try
                {
                    while (!disposed)
                    {
                        using (TcpClient socket = listener.AcceptTcpClient())
                        {
                            current = socket;
                            Interlocked.Increment(ref Accepted);
                            using (NetworkStream stream = socket.GetStream())
                            {
                                var bytes = new List<byte>();
                                while (bytes.Count < 16384)
                                {
                                    int next = stream.ReadByte();
                                    if (next < 0) throw new EndOfStreamException();
                                    bytes.Add((byte)next);
                                    int n = bytes.Count;
                                    if (n >= 4 && bytes[n-4] == 13 && bytes[n-3] == 10 && bytes[n-2] == 13 && bytes[n-1] == 10) break;
                                }
                                if (RejectRemaining > 0)
                                {
                                    Interlocked.Decrement(ref RejectRemaining);
                                    Write(stream, "HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                                    continue;
                                }
                                HandshakeGate.Wait();
                                if (disposed) return;
                                string key = null;
                                foreach (string line in Encoding.ASCII.GetString(bytes.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None))
                                    if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)) key = line.Substring(18).Trim();
                                using (SHA1 sha = SHA1.Create())
                                    Write(stream, "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " +
                                        Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"))) + "\r\n\r\n");
                                release.Wait();
                            }
                        }
                    }
                }
                catch (IOException) { }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            }

            private static void Write(Stream stream, string text)
            {
                byte[] bytes = Encoding.ASCII.GetBytes(text);
                stream.Write(bytes, 0, bytes.Length);
            }

            public void Dispose()
            {
                disposed = true;
                HandshakeGate.Set(); release.Set(); listener.Stop(); current?.Dispose();
            }
        }
    }
}
