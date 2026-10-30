using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace EmbodiedNPC.Tests
{
    public sealed class RecoveryQueryRecordingClient : BackendWebSocketClient
    {
        public readonly List<CognitiveRequest> Queries = new();
        public override Task<bool> QueryTaskCommandAsync(CognitiveRequest request)
        { Queries.Add(request); return Task.FromResult(true); }
    }
    public sealed class CognitiveRecoveryOrderingTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject root;
        private RecoveryQueryRecordingClient client;
        private CancellationTokenSource lifetime;
        private Dictionary<string,CognitiveRequest> Pending => (Dictionary<string,CognitiveRequest>)
            typeof(BackendWebSocketClient).GetField("pendingTaskCommands",Private).GetValue(client);
        [SetUp] public void SetUp()
        {
            root = new GameObject("RecoveryOrdering");
            client = root.AddComponent<RecoveryQueryRecordingClient>();
            typeof(BackendWebSocketClient).GetField("connectOnStart",Private).SetValue(client,false);
            lifetime = new CancellationTokenSource();
            typeof(BackendWebSocketClient).GetField("lifetime",Private).SetValue(client,lifetime);
        }
        [TearDown] public void TearDown() { Object.DestroyImmediate(root); lifetime.Dispose(); }
        private void Open() => typeof(BackendWebSocketClient).GetMethod("HandleOpen",Private).Invoke(client,null);
        [Test] public void ConnectedObserverNewRequestIsNotHistoricalRecovery()
        {
            var fresh = new CognitiveRequest { request_id="fresh" };
            client.ConnectionStatusChanged += status => { if(status=="CONNECTED") Pending[fresh.request_id]=fresh; };
            Open();
            Assert.That(client.Queries,Is.Empty,"A CONNECTED observer submitted on this connection; it is not a lost old command.");
        }
        [Test] public void ReconnectQueriesOnlyPreviouslyPendingRequests()
        {
            var old = new CognitiveRequest { request_id="old" };
            var fresh = new CognitiveRequest { request_id="fresh" };
            Pending[old.request_id]=old;
            client.ConnectionStatusChanged += status => { if(status=="CONNECTED") Pending[fresh.request_id]=fresh; };
            Open();
            Assert.That(client.Queries,Is.EqualTo(new[]{old}));
        }
    }
}
