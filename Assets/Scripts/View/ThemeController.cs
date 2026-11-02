using System;
using UnityEngine;

namespace AgentConsole.View
{
    /// <summary>
    /// Owns the active theme and pushes it into the world: URP exponential fog and the camera clear
    /// colour both come from the theme's background, because fog is the strongest depth cue in this scene.
    /// Everything else subscribes to <see cref="Changed"/> and restyles itself.
    /// </summary>
    public sealed class ThemeController : MonoBehaviour
    {
        private const string PrefKey = "agc.theme.v1";

        [SerializeField] private string presetKey = ThemePresets.DefaultKey;
        [SerializeField, Range(0.002f, 0.1f)] private float fogDensity = 0.02f;
        [SerializeField] private Camera targetCamera;
        [SerializeField] private bool rememberChoice = true;

        private ThemeTokens _tokens;

        public event Action<ThemeTokens> Changed;

        public string PresetKey => presetKey;
        public float FogDensity => fogDensity;

        public ThemeTokens Tokens
        {
            get
            {
                if (_tokens == null) _tokens = ThemeTokens.Compute(ThemePresets.BaseOf(ThemePresets.Find(presetKey)));
                return _tokens;
            }
        }

        private void Awake()
        {
            if (rememberChoice && Application.isPlaying)
            {
                string saved = PlayerPrefs.GetString(PrefKey, null);
                if (!string.IsNullOrEmpty(saved)) presetKey = ThemePresets.Find(saved).Key;
            }
            Apply(presetKey);
        }

        public void SetCamera(Camera cam) => targetCamera = cam;

        public void Apply(string key)
        {
            ThemePreset preset = ThemePresets.Find(key);
            presetKey = preset.Key;
            _tokens = ThemeTokens.Compute(ThemePresets.BaseOf(preset));
            PushToWorld();
            if (rememberChoice && Application.isPlaying)
            {
                PlayerPrefs.SetString(PrefKey, presetKey);
            }
            Changed?.Invoke(_tokens);
        }

        private void PushToWorld()
        {
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogDensity = fogDensity;
            RenderSettings.fogColor = _tokens.Bg;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = _tokens.Bg;
            if (targetCamera != null)
            {
                targetCamera.clearFlags = CameraClearFlags.SolidColor;
                targetCamera.backgroundColor = _tokens.Bg;
            }
        }

        private void OnValidate()
        {
            if (Application.isPlaying && _tokens != null)
            {
                RenderSettings.fogDensity = fogDensity;
            }
        }
    }
}
