using System;
using System.Collections.Generic;
using EmbodiedNPC.Communication;
using Newtonsoft.Json;
using UnityEngine;

namespace EmbodiedNPC.Memory
{
    /// <summary>Bounded in-memory append queue; durability begins only after Backend ACK.</summary>
    public sealed class ExperienceWriteCoordinator
    {
        private const int QueueCapacity = 128;
        private const int MaxAttempts = 5;
        private const double AcknowledgementTimeoutSeconds = 3d;
        private const double RetryDelaySeconds = 1d;
        private readonly BackendWebSocketClient client;
        private readonly List<PendingAppend> pending = new();
        private readonly HashSet<string> pendingDrafts = new(StringComparer.Ordinal);
        private bool sendInProgress;

        public ExperienceWriteCoordinator(BackendWebSocketClient client) =>
            this.client = client ?? throw new ArgumentNullException(nameof(client));

        public int PendingCount => pending.Count;
        public int PersistedCount { get; private set; }
        public int FailedCount { get; private set; }
        public string LastStatus { get; private set; } = "idle";
        public event Action<ExperienceDraftDto, ValidatedExperienceAppendResult, string> AppendSettled;

        public bool Enqueue(ExperienceDraft draft)
        {
            if (draft == null || !draft.IsValid) return false;
            string identity = JsonConvert.SerializeObject(ExperienceDraftDto.FromDomain(draft), Formatting.None);
            if (pendingDrafts.Contains(identity)) return false;
            if (pending.Count >= QueueCapacity)
            {
                FailedCount++;
                LastStatus = "queue_full";
                PublishSettled(ExperienceDraftDto.FromDomain(draft), null, LastStatus);
                return false;
            }
            pending.Add(new PendingAppend(ExperienceAppendRequest.FromDraft(draft), identity));
            pendingDrafts.Add(identity);
            LastStatus = "queued";
            return true;
        }

        public void OnConnected()
        {
            foreach (PendingAppend value in pending)
            {
                value.AwaitingAcknowledgement = false;
                value.NextAttemptAt = 0d;
            }
        }

        public void HandleResult(ValidatedExperienceAppendResult result)
        {
            if (result == null || pending.Count == 0) return;
            PendingAppend head = pending[0];
            if (result.RequestId != head.Request.request_id || result.TraceId != head.Request.trace_id ||
                result.ObserverId != head.Request.draft.observer_id) return;
            if (result.IsPersisted)
            {
                RemoveHead();
                PersistedCount++;
                LastStatus = result.Status;
                PublishSettled(head.Request.draft, result, LastStatus);
            }
            else if (result.Status == "storage_failed" && head.Attempts < MaxAttempts)
            {
                head.AwaitingAcknowledgement = false;
                head.NextAttemptAt = Time.realtimeSinceStartupAsDouble + RetryDelaySeconds;
                LastStatus = result.Status;
            }
            else
            {
                RemoveHead();
                FailedCount++;
                LastStatus = result.Status;
                PublishSettled(head.Request.draft, result, LastStatus);
            }
        }

        public void Tick(double now)
        {
            if (sendInProgress || pending.Count == 0 || !client.IsConnected) return;
            PendingAppend head = pending[0];
            if (now < head.NextAttemptAt) return;
            if (head.Attempts >= MaxAttempts)
            {
                RemoveHead();
                FailedCount++;
                LastStatus = "retry_exhausted";
                PublishSettled(head.Request.draft, null, LastStatus);
                return;
            }
            SendHeadAsync(head);
        }

        private async void SendHeadAsync(PendingAppend head)
        {
            sendInProgress = true;
            bool sent = false;
            try { sent = await client.SendExperienceAppendRequestAsync(head.Request); }
            catch (Exception exception)
            { Debug.LogWarning($"[Experience] send failed: {exception.GetType().Name}", client); }
            sendInProgress = false;
            if (pending.Count == 0 || !ReferenceEquals(head, pending[0])) return;
            head.Attempts++;
            head.AwaitingAcknowledgement = sent;
            head.NextAttemptAt = Time.realtimeSinceStartupAsDouble +
                                 (sent ? AcknowledgementTimeoutSeconds : RetryDelaySeconds);
            LastStatus = sent ? "awaiting_ack" : "send_failed";
        }

        private void RemoveHead()
        {
            pendingDrafts.Remove(pending[0].Identity);
            pending.RemoveAt(0);
        }

        private void PublishSettled(ExperienceDraftDto draft, ValidatedExperienceAppendResult result, string status)
        {
            foreach (Delegate subscriber in AppendSettled?.GetInvocationList() ?? Array.Empty<Delegate>())
                try { ((Action<ExperienceDraftDto, ValidatedExperienceAppendResult, string>)subscriber)(draft, result, status); }
                catch (Exception exception) { Debug.LogWarning($"[Experience] settlement observer: {exception.GetType().Name}"); }
        }

        private sealed class PendingAppend
        {
            public PendingAppend(ExperienceAppendRequest request, string identity)
            { Request = request; Identity = identity; }
            public ExperienceAppendRequest Request { get; }
            public string Identity { get; }
            public int Attempts { get; set; }
            public bool AwaitingAcknowledgement { get; set; }
            public double NextAttemptAt { get; set; }
        }
    }
}
