using UnityEngine;
using UnityEngine.AI;

namespace EmbodiedNPC.Navigation
{
    /// <summary>
    /// M2.2 的最小寻路验证组件：只负责让所属 NPC 走到一个已显式指定的场景目标点。
    /// 不处理拾取、物品归属、动画或后端消息。
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public sealed class WalkToDebugRunner : MonoBehaviour
    {
        [Header("Destination")]
        [SerializeField]
        [Tooltip("NPC 应到达的场景目标点，例如 CupInteractPoint。")]
        private Transform destination;

        [SerializeField]
        [Tooltip("进入播放模式时自动发起一次寻路，用于 M2.2 验证。")]
        private bool autoStartOnStart = true;

        [SerializeField, Min(0f)]
        [Tooltip("在 NavMeshAgent 停止距离之外额外允许的到达误差。")]
        private float arrivalTolerance = 0.05f;

        private NavMeshAgent agent;
        private bool navigationInProgress;

        private void Awake()
        {
            agent = GetComponent<NavMeshAgent>();
        }

        private void Start()
        {
            if (autoStartOnStart)
            {
                StartWalkToDestination();
            }
        }

        private void Update()
        {
            if (!navigationInProgress || agent.pathPending)
            {
                return;
            }

            if (agent.pathStatus == NavMeshPathStatus.PathInvalid)
            {
                FinishWithFailure("PATH_NOT_FOUND", "NavMesh 无法生成到目标点的有效路径。");
                return;
            }

            if (agent.pathStatus == NavMeshPathStatus.PathPartial)
            {
                FinishWithFailure("TARGET_UNREACHABLE", "目标点只生成了部分路径，NPC 无法真正到达。");
                return;
            }

            if (agent.remainingDistance <= agent.stoppingDistance + arrivalTolerance)
            {
                navigationInProgress = false;
                Debug.Log($"[WalkToDebugRunner] SUCCESS: 已到达 '{destination.name}'。", this);
            }
        }

        /// <summary>
        /// 请求 NavMeshAgent 移动到 Inspector 指定的目标点。
        /// </summary>
        public void StartWalkToDestination()
        {
            if (destination == null)
            {
                FinishWithFailure("TARGET_MISSING", "未设置 destination 引用。");
                return;
            }

            if (agent == null)
            {
                FinishWithFailure("AGENT_MISSING", "未找到 NavMeshAgent 组件。");
                return;
            }

            if (!agent.isOnNavMesh)
            {
                FinishWithFailure("AGENT_NOT_ON_NAVMESH", "NPC 当前不在已烘焙的 NavMesh 上。");
                return;
            }

            navigationInProgress = agent.SetDestination(destination.position);

            if (!navigationInProgress)
            {
                FinishWithFailure("PATH_NOT_FOUND", "NavMeshAgent 拒绝了目标位置。");
                return;
            }

            Debug.Log($"[WalkToDebugRunner] 开始寻路：{transform.position} → {destination.name} {destination.position}。", this);
        }

        private void FinishWithFailure(string code, string reason)
        {
            navigationInProgress = false;
            Debug.LogError($"[WalkToDebugRunner] {code}: {reason}", this);
        }
    }
}
