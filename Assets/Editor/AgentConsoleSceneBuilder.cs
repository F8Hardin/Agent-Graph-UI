using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using AgentConsole.App;
using AgentConsole.View;

namespace AgentConsole.Editor
{
    /// <summary>
    /// Builds Assets/Scenes/AgentGraphConsole.unity from nothing, so the scene is reproducible rather than
    /// hand-wired: camera rig, graph root, ambient particles, canvas, a global URP Volume (Bloom) and the
    /// bootstrap. Idempotent: running it again rebuilds the scene and reuses the material assets.
    /// Fog is not in the Volume because URP has no fog override; it is RenderSettings fog, driven by the
    /// ThemeController from the theme's background colour.
    /// </summary>
    public static class AgentConsoleSceneBuilder
    {
        private const string ScenePath = "Assets/Scenes/AgentGraphConsole.unity";
        private const string MatDir = "Assets/Materials";
        private const string SettingsDir = "Assets/Settings";

        [MenuItem("Agent Console/Build Scene")]
        public static void Build()
        {
            Directory.CreateDirectory(MatDir);
            Directory.CreateDirectory(SettingsDir);
            Directory.CreateDirectory("Assets/Scenes");

            Material sphereMat = MakeMat(MatDir + "/AgentNodeSphere.mat", "AgentConsole/NodeSphere");
            Material plasmaMat = MakeMat(MatDir + "/AgentNodePlasma.mat", "AgentConsole/NodePlasma");
            Material edgeMat = MakeMat(MatDir + "/AgentEdge.mat", "AgentConsole/Edge");
            Material particleMat = MakeMat(MatDir + "/AgentParticle.mat", "AgentConsole/Particle");
            VolumeProfile profile = MakeProfile(SettingsDir + "/AgentConsoleVolume.asset");
            AssetDatabase.SaveAssets();

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Theme first: it owns fog and the clear colour.
            GameObject themeGo = new GameObject("Theme");
            ThemeController theme = themeGo.AddComponent<ThemeController>();

            // Camera rig.
            GameObject rigGo = new GameObject("Camera Rig");
            GameObject camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            camGo.transform.SetParent(rigGo.transform, false);
            camGo.transform.position = new Vector3(0f, 6f, -26f);
            Camera cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 42f;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 300f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.allowHDR = true;
            camGo.AddComponent<AudioListener>();
            UniversalAdditionalCameraData extra = camGo.AddComponent<UniversalAdditionalCameraData>();
            extra.renderPostProcessing = true;
            extra.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            CameraRig rig = rigGo.AddComponent<CameraRig>();

            // Graph root.
            GameObject graphGo = new GameObject("Graph Root");
            GraphView graph = graphGo.AddComponent<GraphView>();

            // Ambient particles.
            GameObject partGo = new GameObject("Ambient Particles");
            partGo.AddComponent<ParticleSystem>();
            AmbientParticles particles = partGo.AddComponent<AmbientParticles>();
            partGo.GetComponent<ParticleSystemRenderer>().sharedMaterial = particleMat;

            // Canvas.
            GameObject canvasGo = new GameObject("Console UI", typeof(RectTransform));
            canvasGo.layer = 5;
            Canvas canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasGo.AddComponent<CanvasScaler>();
            canvasGo.AddComponent<GraphicRaycaster>();
            ConsoleUI ui = canvasGo.AddComponent<ConsoleUI>();

            GameObject esGo = new GameObject("EventSystem");
            esGo.AddComponent<EventSystem>();
            esGo.AddComponent<InputSystemUIInputModule>().AssignDefaultActions();

            // Global Volume: mild Bloom.
            GameObject volGo = new GameObject("Global Volume");
            Volume vol = volGo.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.priority = 1f;
            vol.sharedProfile = profile;

            // View facade + composition root.
            GameObject consoleGo = new GameObject("Console");
            ConsoleView view = consoleGo.AddComponent<ConsoleView>();
            ConsoleBootstrap boot = consoleGo.AddComponent<ConsoleBootstrap>();

            // Wire serialized references.
            Set(theme, "targetCamera", cam);
            Set(rig, "viewCamera", cam);
            Set(rig, "graphRoot", graphGo.transform);
            Set(graph, "sphereMaterial", sphereMat);
            Set(graph, "plasmaMaterial", plasmaMat);
            Set(graph, "edgeMaterial", edgeMat);
            Set(graph, "theme", theme);
            Set(graph, "viewCamera", cam);
            Set(particles, "particleMaterial", particleMat);
            Set(particles, "theme", theme);
            Set(ui, "theme", theme);
            Set(view, "graph", graph);
            Set(view, "rig", rig);
            Set(view, "ui", ui);
            Set(view, "theme", theme);
            Set(boot, "view", view);

            theme.SetCamera(cam);
            theme.Apply(ThemePresets.DefaultKey);

            EditorSceneManager.SaveScene(scene, ScenePath);

            // Make it the first scene in the build.
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[AgentConsole] Built " + ScenePath);
        }

        private static void Set(Object target, string field, Object value)
        {
            SerializedObject so = new SerializedObject(target);
            SerializedProperty p = so.FindProperty(field);
            if (p == null)
            {
                Debug.LogError("[AgentConsole] " + target.GetType().Name + " has no serialized field '" + field + "'");
                return;
            }
            p.objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static Material MakeMat(string path, string shaderName)
        {
            Shader shader = Shader.Find(shaderName);
            if (shader == null)
            {
                Debug.LogError("[AgentConsole] Shader not found: " + shaderName);
                return null;
            }
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
            {
                existing.shader = shader;
                return existing;
            }
            Material m = new Material(shader);
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        private static VolumeProfile MakeProfile(string path)
        {
            VolumeProfile profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, path);
            }
            if (!profile.TryGet(out Bloom bloom)) bloom = profile.Add<Bloom>(true);
            bloom.active = true;
            bloom.threshold.Override(0.85f);
            bloom.intensity.Override(0.55f);
            bloom.scatter.Override(0.6f);
            bloom.clamp.Override(40f);
            EditorUtility.SetDirty(profile);
            return profile;
        }
    }
}
