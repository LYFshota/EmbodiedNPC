using EmbodiedNPC.World;
using UnityEngine;
using UnityEngine.AI;

namespace EmbodiedNPC.Interaction
{
    /// <summary>
    /// Unity-authoritative state and authored interaction anchors for one door.
    /// Only WorldStateService may mutate the runtime open state.
    /// </summary>
    public sealed class DoorState : MonoBehaviour
    {
        [SerializeField] private WorldEntity door;
        [SerializeField] private Transform approachPoint;
        [SerializeField] private Transform movingPanel;
        [SerializeField] private NavMeshObstacle blockingObstacle;
        [SerializeField] private bool initiallyOpen;
        [SerializeField] private bool locked;
        [SerializeField] private float openAngle = 90f;

        private bool initialized;
        private bool isOpen;
        private Quaternion closedLocalRotation;

        public WorldEntity Door => door;
        public Transform ApproachPoint => approachPoint;
        public bool IsOpen => isOpen;
        public bool IsLocked => locked;
        public bool IsConfigured => door != null && door.gameObject == gameObject && approachPoint != null &&
                                    movingPanel != null && blockingObstacle != null;

        private void Awake()
        {
            if (door == null) door = GetComponent<WorldEntity>();
            InitializePresentation();
        }

        private void OnValidate()
        {
            if (door == null) door = GetComponent<WorldEntity>();
        }

        internal DoorRuntimeSnapshot CaptureRuntimeState()
        {
            EnsureInitialized();
            return new DoorRuntimeSnapshot(isOpen, movingPanel != null ? movingPanel.localRotation : Quaternion.identity,
                blockingObstacle != null && blockingObstacle.enabled);
        }

        internal bool TryOpenFromAuthority()
        {
            EnsureInitialized();
            if (!IsConfigured || locked || isOpen) return false;
            ApplyOpenState(true);
            return true;
        }

        internal void RestoreRuntimeState(DoorRuntimeSnapshot snapshot)
        {
            EnsureInitialized();
            isOpen = snapshot.IsOpen;
            if (movingPanel != null) movingPanel.localRotation = snapshot.PanelLocalRotation;
            if (blockingObstacle != null) blockingObstacle.enabled = snapshot.ObstacleEnabled;
        }

        private void EnsureInitialized()
        {
            if (!initialized) InitializePresentation();
        }

        private void InitializePresentation()
        {
            if (initialized) return;
            closedLocalRotation = movingPanel != null ? movingPanel.localRotation : Quaternion.identity;
            initialized = true;
            ApplyOpenState(initiallyOpen);
        }

        private void ApplyOpenState(bool open)
        {
            isOpen = open;
            if (movingPanel != null)
                movingPanel.localRotation = closedLocalRotation * Quaternion.Euler(0f, open ? openAngle : 0f, 0f);
            if (blockingObstacle != null)
                blockingObstacle.enabled = !open;
        }
    }

    internal readonly struct DoorRuntimeSnapshot
    {
        public DoorRuntimeSnapshot(bool isOpen, Quaternion panelLocalRotation, bool obstacleEnabled)
        {
            IsOpen = isOpen;
            PanelLocalRotation = panelLocalRotation;
            ObstacleEnabled = obstacleEnabled;
        }

        public bool IsOpen { get; }
        public Quaternion PanelLocalRotation { get; }
        public bool ObstacleEnabled { get; }
    }
}
