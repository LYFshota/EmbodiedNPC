using EmbodiedNPC.Perception;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EmbodiedNPC.UI
{
    /// <summary>Presentation-only toggle for the selected scene's NPC vision debug renderer.</summary>
    [RequireComponent(typeof(Button))]
    public sealed class VisionConeToggleButton : MonoBehaviour
    {
        [SerializeField] private Button button;
        [SerializeField] private TMP_Text label;
        [SerializeField] private VisionConeDebugRenderer visionRenderer;

        private void Awake()
        {
            if (button == null) button = GetComponent<Button>();
            if (label == null) label = GetComponentInChildren<TMP_Text>();
            if (visionRenderer == null) visionRenderer = FindAnyObjectByType<VisionConeDebugRenderer>();
        }

        private void OnEnable()
        {
            if (button != null) button.onClick.AddListener(ToggleVision);
            RefreshLabel();
        }

        private void OnDisable()
        {
            if (button != null) button.onClick.RemoveListener(ToggleVision);
        }

        private void ToggleVision()
        {
            visionRenderer?.Toggle();
            RefreshLabel();
        }

        private void RefreshLabel()
        {
            if (label == null) return;
            label.text = visionRenderer != null && visionRenderer.IsVisible
                ? "关闭 NPC 视野"
                : "显示 NPC 视野";
        }
    }
}
