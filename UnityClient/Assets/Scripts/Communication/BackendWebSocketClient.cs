using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbodiedNPC.Memory;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.World;
using NativeWebSocket;
using UnityEngine;

namespace EmbodiedNPC.Communication
{
    /// <summary>Transport and shared response ingress. Disconnect invalidates pending proposals;
    /// an already accepted deterministic plan continues independently.</summary>
    public class BackendWebSocketClient : MonoBehaviour
    {
        public event Action<ValidatedExperienceAppendResult> ValidatedExperienceAppendResultReceived;
        public event Action<ExperienceDraftDto, ValidatedExperienceAppendResult, string> ExperienceAppendSettled;
        public event Action<CognitiveResponse> CognitiveResponseReceived;
        public event Action<TaskRuntimeSnapshot> TaskRuntimeSnapshotReceived;
        public event Action<TaskExecutionResultDto> TaskExecutionResultReceived;
        public event Action<Newtonsoft.Json.Linq.JObject> TaskCancelResultReceived;
        public event Action<CognitiveTransportFailure> CognitiveRequestFailed;
        public event Action<string> ConnectionStatusChanged;
        [SerializeField] private string serverUrl = "ws://127.0.0.1:8000/ws";
        [SerializeField] private WorldStateService worldState;
        [SerializeField] private bool connectOnStart = true;
        [SerializeField, Min(1)] private int maxConnectAttempts = 3;
        [SerializeField, Min(0.1f)] private float retryDelaySeconds = 1f;
        [SerializeField, Min(0.1f)] private float connectTimeoutSeconds = 5f;
        private WebSocket websocket;
        private CancellationTokenSource lifetime;
        private Task connectionTask;
        private bool connectionStarting;
        private readonly Dictionary<string, CognitiveRequest> pendingTaskCommands = new(StringComparer.Ordinal);
        private ExperienceWriteCoordinator experienceWrites;
        public virtual bool IsConnected => websocket != null && websocket.State == WebSocketState.Open;
        public string HttpBaseUrl
        {
            get
            {
                var uri = new UriBuilder(serverUrl);
                uri.Scheme = uri.Scheme == "wss" ? "https" : "http";
                uri.Path = string.Empty;
                uri.Query = string.Empty;
                return uri.Uri.AbsoluteUri.TrimEnd('/');
            }
        }
        public string HttpUrl(string path) => HttpBaseUrl + "/" + path.TrimStart('/');
        public string DialogueHistoryUrl => HttpUrl("v1/dialogue-history");
        public string ConnectionStatus { get; private set; } = "OFFLINE";
        public int ConnectionAttempts { get; private set; }
        public int PendingExperienceCount => ExperienceWrites.PendingCount;
        public int CognitiveResponseDispatchCount { get; private set; }
        public int CognitiveRequestSendCount { get; private set; }
        public string LastCognitiveIngressStatus { get; private set; } = string.Empty;
        public string ExperienceWriteStatus => ExperienceWrites.LastStatus;
        public int CurrentWorldRevision => worldState == null ? 0 : worldState.WorldRevision;
        private ExperienceWriteCoordinator ExperienceWrites
        {
            get
            {
                if (experienceWrites == null)
                {
                    experienceWrites = new ExperienceWriteCoordinator(this);
                    experienceWrites.AppendSettled += (draft, result, status) =>
                        ExperienceAppendSettled?.Invoke(draft, result, status);
                }
                return experienceWrites;
            }
        }

        private void Awake()
        {
            // Both Title and Cafe derive HTTP from this same endpoint before their startup checks.
            string[] arguments = Environment.GetCommandLineArgs();
            for (int i = 1; i < arguments.Length; i++)
            {
                if (arguments[i] != "--backend-url") continue;
                if (i + 1 < arguments.Length && Uri.TryCreate(arguments[i + 1], UriKind.Absolute, out var endpoint) &&
                    (endpoint.Scheme == "ws" || endpoint.Scheme == "wss") && endpoint.IsLoopback &&
                    endpoint.AbsolutePath == "/ws" && endpoint.UserInfo.Length == 0 &&
                    endpoint.Query.Length == 0 && endpoint.Fragment.Length == 0)
                {
                    serverUrl = endpoint.AbsoluteUri;
                    Debug.Log($"[WebSocket] startup endpoint={serverUrl}", this);
                }
                else Debug.LogError("[WebSocket] Invalid --backend-url; expected a loopback ws://host:port/ws URL.", this);
                break;
            }
            if (worldState == null) worldState = FindAnyObjectByType<WorldStateService>();
        }
        private void Start() { if (connectOnStart) ConnectFromContextMenu(); }
        private void Update()
        {
#if !UNITY_WEBGL || UNITY_EDITOR
            websocket?.DispatchMessageQueue();
#endif
            experienceWrites?.Tick(Time.realtimeSinceStartupAsDouble);
        }

        [ContextMenu("连接 Python 后端")]
        public void ConnectFromContextMenu() => EnsureConnected();

        /// <summary>Re-arm one existing bounded round when offline; never owns Backend processes.</summary>
        public virtual bool EnsureConnected()
        {
            if (EmbodiedNPC.Settings.PortableDataPaths.StartupError != null)
            {
                SetStatus(EmbodiedNPC.Settings.PortableDataPaths.StartupError);
                return false;
            }
            if (!isActiveAndEnabled) return false;
            if (IsConnected || connectionStarting || (connectionTask != null && !connectionTask.IsCompleted)) return true;
            // Reserve before CONNECTING observers run synchronously inside the async method.
            connectionStarting = true;
            try
            {
                lifetime?.Dispose(); // The previous task has completed; it no longer uses this CTS.
                lifetime = new CancellationTokenSource();
                connectionTask = ConnectLoopAsync(lifetime.Token);
                return true;
            }
            finally { connectionStarting = false; }
        }

        private async Task ConnectLoopAsync(CancellationToken token)
        {
            ConnectionAttempts = 0;
            try
            {
                for (int attempt = 0; attempt < maxConnectAttempts && !token.IsCancellationRequested; attempt++)
                {
                    await DisposeSocketAsync();
                    ConnectionAttempts++;
                    SetStatus("CONNECTING");
                    if (!EmbodiedNPC.Settings.LocalBackendAccess.TryHeaders(serverUrl, out var headers, out var sessionError))
                    {
                        SetStatus(sessionError);
                        if (attempt + 1 < maxConnectAttempts)
                            await Task.Delay((int)(1000 * retryDelaySeconds * Math.Pow(2, attempt)), token);
                        continue;
                    }
                    websocket = new WebSocket(serverUrl, headers);
                    websocket.OnOpen += HandleOpen;
                    websocket.OnMessage += HandleMessage;
                    websocket.OnError += HandleError;
                    websocket.OnClose += HandleClose;
                    Task session = websocket.Connect();
                    double deadline = Time.realtimeSinceStartupAsDouble + connectTimeoutSeconds;
                    while (!session.IsCompleted && !token.IsCancellationRequested)
                    {
                        if (!IsConnected && Time.realtimeSinceStartupAsDouble >= deadline) { websocket.CancelConnection(); break; }
                        await Task.Delay(25, token);
                    }
                    if (token.IsCancellationRequested)
                    {
                        websocket.CancelConnection();
                        await session;
                        return;
                    }

                    // Connect() completes after the socket is opened. Keep the
                    // session alive so callers can send requests; only cancel a
                    // connection that actually timed out or was explicitly stopped.
                    if (!session.IsCompleted)
                    {
                        websocket.CancelConnection();
                        await session;
                    }

                    while (IsConnected && !token.IsCancellationRequested)
                    {
                        await Task.Delay(100, token);
                    }

                    if (token.IsCancellationRequested) return;
                    if (attempt + 1 < maxConnectAttempts)
                    {
                        SetStatus("RETRY_WAIT");
                        await Task.Delay((int)(1000 * retryDelaySeconds * Math.Pow(2, attempt)), token);
                    }
                }
                SetStatus("OFFLINE");
            }
            catch (OperationCanceledException) { }
            catch (Exception) { SetStatus("CONNECTION_FAILED"); }
            finally { await DisposeSocketAsync(); }
        }

        private void HandleOpen()
        {
            if (!isActiveAndEnabled || lifetime == null || lifetime.IsCancellationRequested) return;
            // CONNECTED observers may synchronously submit on this new socket.
            // Only requests retained before opening belong to recovery.
            var recovering = pendingTaskCommands.Values.ToArray();
            ExperienceWrites.OnConnected();
            SetStatus("CONNECTED");
            foreach (var original in recovering)
                if (pendingTaskCommands.TryGetValue(original.request_id, out var current) && ReferenceEquals(current, original))
                    _ = QueryTaskCommandAsync(original);
        }
        private void HandleError(string ignored) => SetStatus("CONNECTION_FAILED");
        private void HandleClose(WebSocketCloseCode ignored)
        { SetStatus("DISCONNECTED"); }
        private void SetStatus(string status)
        {
            if (this == null) return;
            ConnectionStatus = status;
            Debug.Log($"[WebSocket] status={status}; attempts={ConnectionAttempts}", this);
            ConnectionStatusChanged?.Invoke(status);
        }
        private void HandleMessage(byte[] bytes) => ProcessIncomingJson(Encoding.UTF8.GetString(bytes));
        public bool ProcessIncomingJson(string json)
        {
            if (this == null || !isActiveAndEnabled) return false;
            if(TaskCancelResultWire.TryParse(json,out var cancelResult))
            { TaskCancelResultReceived?.Invoke(cancelResult);return true; }
            if(TaskExecutionResultDto.TryParse(json,out var executionResult))
            { TaskExecutionResultReceived?.Invoke(executionResult);return true; }
            if(TaskRuntimeSnapshot.TryParse(json,out var taskSnapshot))
            { TaskRuntimeSnapshotReceived?.Invoke(taskSnapshot); return true; }
            if (CognitiveResponseValidator.TryParse(json, out CognitiveResponse cognitiveResponse, out _))
            {
                if (pendingTaskCommands.TryGetValue(cognitiveResponse.request_id, out var original) &&
                    cognitiveResponse.trace_id == original.trace_id && cognitiveResponse.run_id == original.run.run_id &&
                    cognitiveResponse.agent_entity_id == original.run.agent_entity_id && TaskReceiptValidator.Matches(cognitiveResponse, original))
                    pendingTaskCommands.Remove(cognitiveResponse.request_id);
                CognitiveResponseDispatchCount++;
                LastCognitiveIngressStatus = "COGNITIVE_RESPONSE_ACCEPTED";
                CognitiveResponseReceived?.Invoke(cognitiveResponse);
                return true;
            }
            if (CognitiveResponseValidator.TryParseFailure(json, out CognitiveTransportFailure cognitiveFailure))
            {
                LastCognitiveIngressStatus = cognitiveFailure.Reason;
                CognitiveRequestFailed?.Invoke(cognitiveFailure);
                return true;
            }
            if (CognitiveResponseValidator.TryDescribeMalformedCognitiveResponse(json,
                    out CognitiveTransportFailure malformedCognitiveResponse))
            {
                LastCognitiveIngressStatus = malformedCognitiveResponse.Reason;
                CognitiveRequestFailed?.Invoke(malformedCognitiveResponse);
                return true;
            }
            if (ExperienceAppendResultValidator.TryParse(json,
                    out ValidatedExperienceAppendResult experienceResult, out _))
            {
                ExperienceWrites.HandleResult(experienceResult);
                ValidatedExperienceAppendResultReceived?.Invoke(experienceResult);
                return true;
            }
            return false;
        }
        public virtual async Task<bool> SendCognitiveRequestAsync(CognitiveRequest request)
        {
            if (!IsConnected || request == null || !request.IsValid) return false;
            if (request.task_runtime != null)
            {
                if (pendingTaskCommands.Count >= 32 && !pendingTaskCommands.ContainsKey(request.request_id))
                    pendingTaskCommands.Remove(pendingTaskCommands.Keys.First());
                pendingTaskCommands[request.request_id] = request;
            }
            try
            {
                await websocket.SendText(request.Serialize());
                CognitiveRequestSendCount++;
                return true;
            }
            catch (Exception) { SetStatus("SEND_FAILED"); return false; }
        }

        public bool EnqueueExperience(ExperienceDraft draft) => ExperienceWrites.Enqueue(draft);

        public virtual async Task<bool> QueryTaskCommandAsync(CognitiveRequest original)
        {
            if (!IsConnected || original?.IsValid != true || original.task_runtime == null) return false;
            try
            {
                var wire = Newtonsoft.Json.Linq.JObject.Parse(original.Serialize());
                await websocket.SendText(new Newtonsoft.Json.Linq.JObject {
                    ["message_type"] = "task_control", ["schema_version"] = "1.1", ["operation"] = "query",
                    ["original_request"] = wire }.ToString(Newtonsoft.Json.Formatting.None));
                return true;
            }
            catch (Exception) { return false; }
        }

        public virtual async Task<bool> SendTaskRuntimeSyncAsync(TaskRuntimeSyncRequestDto request)
        {
            if(!IsConnected || request?.task_runtime?.IsValid!=true || request.recall_identity?.IsValid!=true) return false;
            try { await websocket.SendText(CognitiveRequest.SerializeFields(request)); return true; }
            catch(Exception) { return false; }
        }

        public virtual async Task<bool> SendTaskExecutionControlAsync(Newtonsoft.Json.Linq.JObject control)
        {
            if(!IsConnected || control==null) return false;
            try { await websocket.SendText(control.ToString(Newtonsoft.Json.Formatting.None));return true; }
            catch(Exception) { return false; }
        }

        public virtual async Task<bool> SendCognitiveCancelAsync(CognitiveRequest request)
        {
            if (!IsConnected || (request?.context?.current_event?.event_type != "npc.dialogue_continue" &&
                request?.context?.current_event?.event_type != "player.dwelled_in_service_zone" &&
                request?.context?.current_event?.event_type != "counter.needs_cleaning" && request?.context?.current_event?.event_type != "npc.task_consideration" && request?.action_feedback == null)) return false;
            try
            {
                await websocket.SendText(Newtonsoft.Json.JsonConvert.SerializeObject(new {
                    message_type = "cognitive_cancel", schema_version = "1.0", request_id = request.request_id,
                    trace_id = request.trace_id, run_id = request.run.run_id, agent_entity_id = request.run.agent_entity_id }));
                return true;
            }
            catch (Exception) { return false; }
        }

        public virtual async Task<bool> SendExperienceAppendRequestAsync(ExperienceAppendRequest request)
        {
            if (!IsConnected || request == null || !request.IsValid) return false;
            try
            {
                await websocket.SendText(request.Serialize());
                return true;
            }
            catch (Exception) { SetStatus("SEND_FAILED"); return false; }
        }

        public async Task DisconnectAsync()
        {
            var stoppingLifetime = lifetime;
            var stoppingTask = connectionStarting ? null : connectionTask;
            stoppingLifetime?.Cancel();
            websocket?.CancelConnection();
            // CONNECTING observers can disable us before EnsureConnected assigns the new task.
            if (connectionStarting)
            {
                while (connectionStarting) await Task.Yield();
                if (ReferenceEquals(lifetime, stoppingLifetime)) stoppingTask = connectionTask;
            }
            if (stoppingTask != null) await stoppingTask;
            stoppingLifetime?.Dispose();
            // A completed round may have been explicitly re-armed before this continuation.
            if (ReferenceEquals(lifetime, stoppingLifetime))
            {
                lifetime = null;
                SetStatus("OFFLINE");
            }
        }
        private async Task DisposeSocketAsync()
        {
            var previous = websocket;
            websocket = null;
            if (previous == null) return;
            previous.OnOpen -= HandleOpen; previous.OnMessage -= HandleMessage;
            previous.OnError -= HandleError; previous.OnClose -= HandleClose;
            previous.CancelConnection(); // NativeWebSocket.Connect finally disposes its ClientWebSocket.
            await Task.CompletedTask;
        }
        private async void OnDisable() { await DisconnectAsync(); }
    }
}
