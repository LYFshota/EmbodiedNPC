using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CafeDemo
{
    /// <summary>Only binds the overview slider to the scene's BGM source.</summary>
    public sealed class CafeBgmVolumeSetting : MonoBehaviour
    {
        private CafeBackgroundMusic music;
        private Slider slider;
        private TMP_Text percentage;

        public void Bind(CafeBackgroundMusic backgroundMusic, Slider control, TMP_Text valueLabel)
        {
            Unsubscribe();
            music = backgroundMusic;
            slider = control;
            percentage = valueLabel;
            if (isActiveAndEnabled) Subscribe();
        }

        private void OnEnable() => Subscribe();

        private void Subscribe()
        {
            if (music == null || slider == null) return;
            Unsubscribe();
            slider.SetValueWithoutNotify(music.Volume);
            RefreshPercentage();
            slider.onValueChanged.AddListener(OnVolumeChanged);
        }

        private void OnVolumeChanged(float value)
        {
            music.SetVolume(value);
            RefreshPercentage();
        }

        private void RefreshPercentage() => percentage.text = Mathf.RoundToInt(music.Volume * 100f) + "%";
        private void Unsubscribe() { if (slider != null) slider.onValueChanged.RemoveListener(OnVolumeChanged); }
        private void OnDisable()
        {
            Unsubscribe();
            if (music != null) music.FlushPreferences();
        }
    }
}
