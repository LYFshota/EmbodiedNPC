using System;
using EmbodiedNPC.Perception;
using UnityEngine;
using UnityEngine.AI;

namespace EmbodiedNPC.Planning
{
    public enum RediscoveryStatus { Idle, Searching, Succeeded, Failed, Cancelled }

    /// <summary>No entity registry or target Transform is available at this boundary.</summary>
    public interface ITargetRediscoveryDriver
    {
        void Scan();
        bool CanSee(string entityId);
        bool Face(Vector3 lastKnownPosition, float maxDegrees);
        bool MoveTo(Vector3 lastKnownPosition);
        bool HasArrived { get; }
        bool PathFailed { get; }
        void Turn(float degrees);
        void Stop();
    }

    /// <summary>One bounded attempt: face old evidence, approach it, then scan one revolution.</summary>
    public sealed class TargetRediscovery
    {
        private enum Phase { Face, Approach, Scan }
        private readonly ITargetRediscoveryDriver driver;
        private readonly double timeoutSeconds;
        private Phase phase;
        private Vector3 position;
        private string targetId;
        private double deadline;
        private double nextScanAt;
        private float scannedDegrees;

        public TargetRediscovery(ITargetRediscoveryDriver driver, double timeoutSeconds = 15)
        {
            this.driver = driver ?? throw new ArgumentNullException(nameof(driver));
            this.timeoutSeconds = Math.Max(1, timeoutSeconds);
        }

        public RediscoveryStatus Status { get; private set; }
        public string FailureCode { get; private set; } = string.Empty;
        public Vector3 SearchPosition => position;

        public void Begin(string entityId, Vector3 lastKnownPosition, double now)
        {
            if (Status == RediscoveryStatus.Searching) throw new InvalidOperationException("Rediscovery already active.");
            targetId = entityId;
            position = lastKnownPosition; // immutable evidence, never a live target binding
            phase = Phase.Face;
            scannedDegrees = 0;
            deadline = now + timeoutSeconds;
            nextScanAt = now;
            FailureCode = string.Empty;
            Status = RediscoveryStatus.Searching;
        }

        public void Tick(double now, float deltaTime)
        {
            if (Status != RediscoveryStatus.Searching) return;
            if (now >= deadline) { Finish(RediscoveryStatus.Failed, "REDISCOVERY_TIMEOUT"); return; }
            if (now >= nextScanAt)
            {
                driver.Scan();
                nextScanAt = now + 0.1;
                if (driver.CanSee(targetId)) { Finish(RediscoveryStatus.Succeeded, string.Empty); return; }
            }
            float degrees = Math.Min(30f, Math.Max(0f, deltaTime) * 120f);
            switch (phase)
            {
                case Phase.Face:
                    if (!driver.Face(position, degrees)) return;
                    driver.Scan();
                    if (driver.CanSee(targetId)) { Finish(RediscoveryStatus.Succeeded, string.Empty); return; }
                    if (!driver.MoveTo(position)) { Finish(RediscoveryStatus.Failed, "REDISCOVERY_PATH_UNAVAILABLE"); return; }
                    phase = Phase.Approach;
                    break;
                case Phase.Approach:
                    if (driver.PathFailed) { Finish(RediscoveryStatus.Failed, "REDISCOVERY_PATH_UNAVAILABLE"); return; }
                    if (!driver.HasArrived) return;
                    driver.Stop();
                    phase = Phase.Scan;
                    break;
                case Phase.Scan:
                    if (driver.PathFailed) { Finish(RediscoveryStatus.Failed, "REDISCOVERY_PATH_UNAVAILABLE"); return; }
                    driver.Turn(degrees);
                    scannedDegrees += degrees;
                    if (scannedDegrees < 360f) return;
                    driver.Scan();
                    Finish(driver.CanSee(targetId) ? RediscoveryStatus.Succeeded : RediscoveryStatus.Failed,
                        driver.CanSee(targetId) ? string.Empty : "TARGET_NOT_REDISCOVERED");
                    break;
            }
        }

        public void Cancel(string reason)
        {
            if (Status == RediscoveryStatus.Searching) Finish(RediscoveryStatus.Cancelled, reason);
        }

        private void Finish(RediscoveryStatus status, string code)
        {
            driver.Stop();
            Status = status;
            FailureCode = code;
        }
    }

    public sealed class NavMeshRediscoveryDriver : ITargetRediscoveryDriver
    {
        private readonly NavMeshAgent agent;
        private readonly VisionSensor sensor;
        private bool ownsPath;
        public NavMeshRediscoveryDriver(NavMeshAgent agent, VisionSensor sensor)
        { this.agent = agent; this.sensor = sensor; }
        private bool Ready => agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh;
        public void Scan() { if (sensor != null && sensor.isActiveAndEnabled) sensor.ScanNow(); }
        public bool CanSee(string entityId)
        {
            if (sensor == null || !sensor.isActiveAndEnabled) return false;
            foreach (string visible in sensor.CurrentVisibleEntityIds) if (visible == entityId) return true;
            return false;
        }
        public bool Face(Vector3 lastKnownPosition, float maxDegrees)
        {
            if (!Ready) return true; // MoveTo returns a structured failure next.
            Vector3 direction = lastKnownPosition - agent.transform.position;
            direction.y = 0;
            if (direction.sqrMagnitude < 0.001f) return true;
            Quaternion rotation = Quaternion.LookRotation(direction);
            agent.transform.rotation = Quaternion.RotateTowards(agent.transform.rotation, rotation, maxDegrees);
            return Quaternion.Angle(agent.transform.rotation, rotation) < 1f;
        }
        public bool MoveTo(Vector3 lastKnownPosition)
        {
            if (!Ready || !NavMesh.SamplePosition(lastKnownPosition, out NavMeshHit hit, 2f, agent.areaMask)) return false;
            var path = new NavMeshPath();
            if (!agent.CalculatePath(hit.position, path) || path.status != NavMeshPathStatus.PathComplete) return false;
            agent.isStopped = false;
            ownsPath = agent.SetPath(path);
            return ownsPath;
        }
        public bool HasArrived => Ready && !agent.pathPending && agent.remainingDistance <= agent.stoppingDistance + 0.15f;
        public bool PathFailed => !Ready || ownsPath && !agent.pathPending && agent.pathStatus != NavMeshPathStatus.PathComplete;
        public void Turn(float degrees) { if (Ready) agent.transform.Rotate(0, degrees, 0, Space.World); }
        public void Stop()
        {
            if (ownsPath && Ready) { agent.isStopped = true; agent.ResetPath(); }
            ownsPath = false;
        }
    }
}
