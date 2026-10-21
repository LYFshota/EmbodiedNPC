using UnityEngine;
using UnityEngine.AI;
using EmbodiedNPC.Planning;

namespace CafeDemo
{
    [RequireComponent(typeof(NavMeshAgent))]
    [DisallowMultipleComponent]
    public sealed class CafeDemoNavMeshTestAgent : MonoBehaviour
    {
        [SerializeField] private Transform[] testPoints;
        [SerializeField] private Transform target;
        [SerializeField, Min(.01f)] private float targetSampleRadius = .35f;
        private NavMeshAgent agent;
        private GoalPlanRunner goalPlanRunner;
        private NavMeshPath path;
        private Transform lastTarget;
        private Vector3 lastTargetPosition;
        private bool moving;
        private string status = "Idle.";

        public NavMeshAgent Agent => agent;
        public Transform[] TestPoints => testPoints;
        public string Status => status;

        private void Awake()
        {
            agent = GetComponent<NavMeshAgent>();
            goalPlanRunner = GetComponent<GoalPlanRunner>();
            path = new NavMeshPath();
        }
        private void Update()
        {
            if (!moving || !agent.isOnNavMesh || agent.pathPending) return;
            if (agent.pathStatus != NavMeshPathStatus.PathComplete) { status = "Path became unreachable."; moving = false; return; }
            if (agent.remainingDistance <= agent.stoppingDistance + .04f && agent.velocity.sqrMagnitude < .02f)
            { status = "Arrived: " + (target ? target.name : "target"); moving = false; }
        }
        public bool MoveTo(Transform destination)
        {
            if (goalPlanRunner != null && goalPlanRunner.HasActiveTask)
            { status = "Action runtime owns navigation."; return false; }
            if (!destination || !agent || !agent.isOnNavMesh) { status = "Agent or target is not on a valid NavMesh."; return false; }
            target = lastTarget = destination; lastTargetPosition = destination.position;
            var filter = new NavMeshQueryFilter { agentTypeID = agent.agentTypeID, areaMask = agent.areaMask };
            NavMeshHit hit;
            if (!NavMesh.SamplePosition(destination.position, out hit, targetSampleRadius, filter) ||
                !agent.CalculatePath(hit.position, path) || path.status != NavMeshPathStatus.PathComplete)
            { agent.ResetPath(); moving = false; status = "Unreachable: " + destination.name; return false; }
            moving = agent.SetPath(path); status = moving ? "Moving to: " + destination.name : "Path rejected.";
            return moving;
        }
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.cyan;
            if (testPoints != null) foreach (var p in testPoints) if (p) Gizmos.DrawWireSphere(p.position + Vector3.up * .08f, .18f);
            if (agent && agent.hasPath)
            { var corners = agent.path.corners; for (int i=1;i<corners.Length;i++) Gizmos.DrawLine(corners[i-1],corners[i]); }
        }
    }
}
