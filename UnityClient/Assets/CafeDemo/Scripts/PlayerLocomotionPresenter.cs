using UnityEngine;

namespace CafeDemo
{
    /// <summary>Projects completed CharacterController movement; input is not movement evidence.</summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(50)]
    public sealed class PlayerLocomotionPresenter : MonoBehaviour
    {
        [SerializeField] private CharacterController controller;
        [SerializeField] private Animator animator;
        [SerializeField, Min(.01f)] private float fullWalkSpeed = 2.6f;
        [SerializeField, Min(0f)] private float dampingSeconds = .12f;
        private static readonly int Speed = Animator.StringToHash("Speed");
        private CafeDemoPlayerController player;
        public float NormalizedSpeed { get; private set; }
        private void Awake() => player = GetComponent<CafeDemoPlayerController>();

        public void Project(float deltaTime)
        {
            if (!isActiveAndEnabled || controller == null || animator == null || !animator.isInitialized) return;
            bool frozen = player != null && player.IsPresentationInputSuppressed;
            NormalizedSpeed = controller.enabled && !frozen
                ? Mathf.Clamp01(Vector3.ProjectOnPlane(controller.velocity, Vector3.up).magnitude / Mathf.Max(.01f, fullWalkSpeed)) : 0f;
            if (frozen) animator.SetFloat(Speed, 0f);
            else animator.SetFloat(Speed, NormalizedSpeed, dampingSeconds, Mathf.Max(0f, deltaTime));
        }
        private void Update() => Project(Time.deltaTime);
        private void OnDisable()
        {
            NormalizedSpeed = 0f;
            if (animator != null && animator.isInitialized) animator.SetFloat(Speed, 0f);
        }
    }
}
