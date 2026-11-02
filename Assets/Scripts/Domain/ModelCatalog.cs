using System.Collections.Generic;

namespace AgentConsole.Domain
{
    /// <summary>One model an agent can be switched to.</summary>
    public sealed class ModelInfo
    {
        public string Id { get; }
        public string DisplayName { get; }

        /// <summary>Resident in the inference server right now.</summary>
        public bool IsLoaded { get; }

        /// <summary>Null when the server does not know it (a model that has never been loaded).</summary>
        public int? MaxContextLength { get; }

        public ModelInfo(string id, string displayName, bool isLoaded, int? maxContextLength)
        {
            Id = id;
            DisplayName = string.IsNullOrEmpty(displayName) ? id : displayName;
            IsLoaded = isLoaded;
            MaxContextLength = maxContextLength;
        }
    }

    /// <summary>What the agent says it can run, and what it is running. A snapshot: it goes stale the moment a swap starts.</summary>
    public sealed class ModelCatalog
    {
        public IReadOnlyList<ModelInfo> Models { get; }

        /// <summary>The model the server reports as resident; null when none is.</summary>
        public string CurrentModelId { get; }

        public int? ActiveContextLength { get; }

        /// <summary>The context the server will load a model at next time. The default for a switch, so a switch does not silently change it.</summary>
        public int? ConfiguredContextLength { get; }

        public float? Temperature { get; }

        public ModelCatalog(
            IReadOnlyList<ModelInfo> models, string currentModelId, int? activeContextLength,
            int? configuredContextLength, float? temperature)
        {
            Models = models ?? new ModelInfo[0];
            CurrentModelId = currentModelId;
            ActiveContextLength = activeContextLength;
            ConfiguredContextLength = configuredContextLength;
            Temperature = temperature;
        }

        public ModelInfo Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            for (int i = 0; i < Models.Count; i++)
            {
                if (Models[i].Id == id) return Models[i];
            }
            return null;
        }
    }
}
