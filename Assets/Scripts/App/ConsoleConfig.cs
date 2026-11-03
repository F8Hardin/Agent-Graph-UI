using System;
using System.IO;
using UnityEngine;

namespace AgentConsole.App
{
    /// <summary>
    /// Runtime connection settings, so a built player can be pointed at a different
    /// EVA without a rebuild. This is the stop-gap until the launch-time connection
    /// screen lands (ROADMAP Phase 1); it deliberately holds only what you need to
    /// reach an agent, and never a credential.
    ///
    /// Lookup order, first hit wins:
    ///   1. config.json beside the executable  -- easy to find, easy to edit
    ///   2. config.json in persistentDataPath  -- survives reinstalling the build
    ///   3. the values serialized in the scene
    /// A default file is written beside the executable on first run so there is
    /// something to edit rather than a format to guess at.
    /// </summary>
    [Serializable]
    public sealed class ConsoleConfig
    {
        public string transport = "EvaSse";          // "EvaSse" or "Replay"
        public string evaBaseUrl = ConsoleBootstrap.DefaultEvaBaseUrl;

        public const string FileName = "config.json";

        /// <summary>The file Load() read, so Save() writes back to the same place and the edit is the one that wins.</summary>
        private static string _loadedFrom;

        public static string BesideExecutable =>
            Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", FileName);

        public static string InPersistentData =>
            Path.Combine(Application.persistentDataPath, FileName);

        /// <summary>Null when no file exists, so the caller keeps its scene values.</summary>
        public static ConsoleConfig Load()
        {
            foreach (string path in new[] { BesideExecutable, InPersistentData })
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    string json = File.ReadAllText(path);
                    ConsoleConfig cfg = JsonUtility.FromJson<ConsoleConfig>(json);
                    if (cfg == null) continue;
                    if (string.IsNullOrWhiteSpace(cfg.evaBaseUrl)) continue;
                    Debug.Log($"[AgentConsole] config from {path}: transport={cfg.transport} url={cfg.evaBaseUrl}");
                    _loadedFrom = path;
                    return cfg;
                }
                catch (Exception e)
                {
                    // A malformed file must not stop the app booting -- fall through
                    // to the next candidate and ultimately to the scene values.
                    Debug.LogWarning($"[AgentConsole] ignoring {path}: {e.Message}");
                }
            }
            return null;
        }

        /// <summary>Best-effort; a read-only install directory is not an error worth failing on.</summary>
        public void WriteIfAbsent()
        {
            try
            {
                if (Application.isEditor) return;
                string path = BesideExecutable;
                if (File.Exists(path)) return;
                File.WriteAllText(path, JsonUtility.ToJson(this, true));
                Debug.Log($"[AgentConsole] wrote default config to {path}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AgentConsole] could not write default config: {e.Message}");
            }
        }

        /// <summary>
        /// Persist a change made in Settings. Goes back to the file that was loaded; with none, to persistentDataPath
        /// (never the project folder in the editor, where a stray config.json would end up in the repo). Applies on the
        /// next launch: the transport is built once at start.
        /// </summary>
        public void Save()
        {
            try
            {
                string path = _loadedFrom ?? InPersistentData;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonUtility.ToJson(this, true));
                _loadedFrom = path;
                Debug.Log($"[AgentConsole] saved config to {path}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AgentConsole] could not save config: {e.Message}");
            }
        }

        public TransportKind Kind =>
            string.Equals(transport, "Replay", StringComparison.OrdinalIgnoreCase)
                ? TransportKind.Replay
                : TransportKind.EvaSse;
    }
}
