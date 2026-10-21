using System.Collections.Generic;
using EmbodiedNPC.Planning;
using EmbodiedNPC.World;
using UnityEngine;
using UnityEngine.AI;

namespace EmbodiedNPC.Interaction
{
    /// <summary>
    /// 通用交互安全边界：停止导航、取消活动 Skill，并恢复所有未持有物的初始表现状态。
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public sealed class InteractionSafetyController : MonoBehaviour
    {
        [SerializeField] private NavMeshAgent agent;

        private readonly Dictionary<string, ItemBaseline> baselines = new();

        private void Awake()
        {
            if (agent == null) agent = GetComponent<NavMeshAgent>();
            CaptureInitialStates();
        }

        private void Start()
        {
            // EntityRegistry 可能在本组件之后完成 Awake；Start 时补一次只读基线采集。
            CaptureInitialStates();
        }

        [ContextMenu("Cancel Current Skill")]
        public void CancelCurrentSkill()
        {
            GoalPlanRunner planRunner = GetComponent<GoalPlanRunner>();
            if (planRunner != null && planRunner.InterruptCurrentPlan("CANCELLED_BY_REQUEST"))
            {
                return;
            }

            GetComponent<DeterministicPickupRunner>()?.Cancel();
            GetComponent<DeterministicGiveRunner>()?.Cancel();
            StopAgent();
            RecoverToSafeState();
        }

        public void RecoverToSafeState()
        {
            StopAgent();
            CaptureInitialStates();

            foreach (KeyValuePair<string, ItemBaseline> pair in baselines)
            {
                ItemBaseline baseline = pair.Value;
                if (baseline.State == null || baseline.Entity == null || baseline.State.IsHeld)
                {
                    // 当前仍被任意实体持有的物体保留其权威所有权和附着关系。
                    continue;
                }

                baseline.Restore();
            }
        }

        [ContextMenu("Capture Current Item Baseline")]
        public void CaptureInitialStates()
        {
            HeldItemState[] items = FindObjectsByType<HeldItemState>();
            foreach (HeldItemState state in items)
            {
                if (state == null) continue;
                WorldEntity entity = state.GetComponent<WorldEntity>();
                if (entity == null || string.IsNullOrWhiteSpace(entity.EntityId) || baselines.ContainsKey(entity.EntityId)) continue;
                baselines.Add(entity.EntityId, new ItemBaseline(entity, state));
            }
        }

        private void StopAgent()
        {
            if (agent == null) return;
            agent.isStopped = true;
            agent.ResetPath();
        }

        private sealed class ItemBaseline
        {
            public ItemBaseline(WorldEntity entity, HeldItemState state)
            {
                Entity = entity;
                State = state;
                Parent = entity.transform.parent;
                Position = entity.transform.position;
                Rotation = entity.transform.rotation;
                Collider = entity.GetComponent<Collider>();
                Body = entity.GetComponent<Rigidbody>();
                ColliderEnabled = Collider == null || Collider.enabled;
                BodyKinematic = Body != null && Body.isKinematic;
            }

            public WorldEntity Entity { get; }
            public HeldItemState State { get; }
            private Transform Parent { get; }
            private Vector3 Position { get; }
            private Quaternion Rotation { get; }
            private Collider Collider { get; }
            private Rigidbody Body { get; }
            private bool ColliderEnabled { get; }
            private bool BodyKinematic { get; }

            public void Restore()
            {
                Entity.transform.SetParent(Parent, true);
                Entity.transform.position = Position;
                Entity.transform.rotation = Rotation;
                if (Collider != null) Collider.enabled = ColliderEnabled;
                if (Body != null) Body.isKinematic = BodyKinematic;
            }
        }
    }
}
