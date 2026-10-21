using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EmbodiedNPC.Settings
{
    /// <summary>Adds a slider affordance while the existing input remains the configuration owner.</summary>
    public sealed class SettingsNumericSlider : MonoBehaviour
    {
        [SerializeField] private Slider slider;
        [SerializeField] private TMP_InputField input;
        public void Bind(Slider control, TMP_InputField field)
        {
            Unsubscribe();
            slider = control;
            input = field;
            Subscribe();
            Sync(input.text);
        }
        private void OnEnable() => Subscribe();
        private void OnDisable() => Unsubscribe();
        private void Subscribe()
        {
            if (slider == null || input == null) return;
            Unsubscribe();
            slider.onValueChanged.AddListener(OnSliderChanged);
            input.onValueChanged.AddListener(Sync);
        }
        private void Unsubscribe()
        {
            if (slider != null) slider.onValueChanged.RemoveListener(OnSliderChanged);
            if (input != null) input.onValueChanged.RemoveListener(Sync);
        }
        private void OnSliderChanged(float value) => input.text = value.ToString("0.##", CultureInfo.InvariantCulture);
        private void Sync(string value)
        {
            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number))
                slider.SetValueWithoutNotify(number);
        }
    }
}
