using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CafeDemo
{
    /// <summary>Serialized scene wiring for the existing runtime-only BGM row binding.</summary>
    [RequireComponent(typeof(CafeBgmVolumeSetting))]
    public sealed class TitleVolumeBinding : MonoBehaviour
    {
        [SerializeField] private CafeBackgroundMusic music;
        [SerializeField] private Slider slider;
        [SerializeField] private TMP_Text percentage;
        private void OnEnable() => GetComponent<CafeBgmVolumeSetting>().Bind(music, slider, percentage);
    }
}
