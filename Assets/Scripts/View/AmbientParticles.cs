using UnityEngine;

namespace AgentConsole.View
{
    /// <summary>
    /// One ambient ParticleSystem and nothing else: a sphere emitter around the graph volume, low
    /// emission, slow noise-driven drift, small additive billboards. It is the depth cue that does the
    /// real work: the motes live in world space, so when the camera orbits the near ones sweep across the
    /// screen faster than the far ones. Collision, lights, shadows and trails are all off.
    /// </summary>
    [RequireComponent(typeof(ParticleSystem))]
    public sealed class AmbientParticles : MonoBehaviour
    {
        [SerializeField] private Material particleMaterial;
        [SerializeField] private ThemeController theme;
        [SerializeField] private float volumeRadius = 26f;
        [SerializeField, Range(30f, 60f)] private float emissionPerSecond = 45f;
        [SerializeField] private Vector2 sizeRange = new Vector2(0.05f, 0.14f);
        [SerializeField] private Vector2 lifetimeRange = new Vector2(18f, 28f);
        [SerializeField, Range(0f, 1f)] private float opacity = 0.55f;

        private ParticleSystem _ps;

        private void Awake()
        {
            _ps = GetComponent<ParticleSystem>();
            if (theme == null) theme = FindAnyObjectByType<ThemeController>();
            Configure();
        }

        private void OnEnable()
        {
            if (theme != null) theme.Changed += OnTheme;
        }

        private void OnDisable()
        {
            if (theme != null) theme.Changed -= OnTheme;
        }

        private void Start()
        {
            if (theme != null) OnTheme(theme.Tokens);
        }

        private void OnTheme(ThemeTokens t)
        {
            if (_ps == null) return;
            Color c = Color.Lerp(t.Fg, t.Flow, 0.35f);
            // Additive blending is invisible on a light background, so light themes use a darker, denser mote.
            c.a = t.Dark ? opacity : Mathf.Min(1f, opacity + 0.2f);
            ParticleSystem.MainModule main = _ps.main;
            main.startColor = new ParticleSystem.MinMaxGradient(c);
        }

        private void Configure()
        {
            ParticleSystem.MainModule main = _ps.main;
            main.loop = true;
            main.prewarm = true;
            main.playOnAwake = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(lifetimeRange.x, lifetimeRange.y);
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(sizeRange.x, sizeRange.y);
            main.maxParticles = Mathf.CeilToInt(emissionPerSecond * lifetimeRange.y * 1.2f);
            main.gravityModifier = 0f;

            ParticleSystem.EmissionModule emission = _ps.emission;
            emission.enabled = true;
            emission.rateOverTime = emissionPerSecond;

            ParticleSystem.ShapeModule shape = _ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = volumeRadius;
            shape.radiusThickness = 1f; // fill the volume, not just the shell

            ParticleSystem.NoiseModule noise = _ps.noise;
            noise.enabled = true;
            noise.strength = 0.35f;
            noise.frequency = 0.12f;
            noise.scrollSpeed = 0.06f;
            noise.damping = true;
            noise.octaveCount = 2;
            noise.quality = ParticleSystemNoiseQuality.Medium;

            // Fade in and out so motes never pop.
            ParticleSystem.ColorOverLifetimeModule col = _ps.colorOverLifetime;
            col.enabled = true;
            Gradient g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.15f), new GradientAlphaKey(1f, 0.85f), new GradientAlphaKey(0f, 1f) });
            col.color = new ParticleSystem.MinMaxGradient(g);

            // Explicitly off.
            ParticleSystem.CollisionModule collision = _ps.collision;
            collision.enabled = false;
            ParticleSystem.LightsModule lights = _ps.lights;
            lights.enabled = false;
            ParticleSystem.TrailModule trails = _ps.trails;
            trails.enabled = false;

            ParticleSystemRenderer r = GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            if (particleMaterial != null) r.sharedMaterial = particleMaterial;

            _ps.Clear();
            _ps.Play();
        }
    }
}
