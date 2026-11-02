using System.Collections.Generic;
using AgentConsole.Domain;

namespace AgentConsole.Transport
{
    /// <summary>
    /// Parses EVA's GET /models and builds the request and error text for POST /set-model.
    /// <code>
    /// {"models":[{"id":...,"display_name":...,"state":"loaded"|"not-loaded","max_context_length":N|null,...}],
    ///  "current_model":...,"active_context_length":N|null,"configured_context_length":N,"temperature":0.6, ...}
    /// </code>
    /// Tolerant like <see cref="WireParser"/>: unknown fields are ignored, a row without an id is skipped, and
    /// every number may be null, because a model that has never been loaded has no known context.
    /// </summary>
    public static class ModelsWire
    {
        public static bool TryParseCatalog(string json, out ModelCatalog catalog)
        {
            catalog = null;
            if (!MiniJson.TryParse(json, out object root) || !(root is Dictionary<string, object> obj)) return false;
            if (!(Get(obj, "models") is List<object> rows)) return false;

            List<ModelInfo> models = new List<ModelInfo>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                if (!(rows[i] is Dictionary<string, object> row)) continue;
                string id = Get(row, "id") as string;
                if (string.IsNullOrEmpty(id)) continue;
                models.Add(new ModelInfo(
                    id, Get(row, "display_name") as string, Get(row, "state") as string == "loaded",
                    AsInt(Get(row, "max_context_length"))));
            }

            float? temperature = Get(obj, "temperature") is double t ? (float?)t : null;
            catalog = new ModelCatalog(
                models, Get(obj, "current_model") as string, AsInt(Get(obj, "active_context_length")),
                AsInt(Get(obj, "configured_context_length")), temperature);
            return true;
        }

        public static string SetModelJson(string modelId, int? contextLength)
        {
            string json = "{\"model\":" + MiniJson.Quote(modelId);
            if (contextLength.HasValue && contextLength.Value > 0)
            {
                json += ",\"context_length\":" + contextLength.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return json + "}";
        }

        /// <summary>
        /// The words to show for a failed request. FastAPI puts its explanation in {"detail": "..."}, and EVA's
        /// /set-model uses that for the load failure ("Saved X, but loading it now failed: ..."), so it is worth
        /// showing verbatim; anything else falls back to the status line.
        /// </summary>
        public static string ErrorText(int status, string reason, string body)
        {
            if (MiniJson.TryParse(body, out object root) && root is Dictionary<string, object> obj)
            {
                object detail = Get(obj, "detail");
                if (detail is string s && s.Length > 0) return s;
                if (detail != null) return MiniJson.Serialize(detail);
            }
            return "EVA returned " + status + (string.IsNullOrEmpty(reason) ? string.Empty : " " + reason) + ".";
        }

        private static object Get(Dictionary<string, object> d, string key)
        {
            d.TryGetValue(key, out object v);
            return v;
        }

        private static int? AsInt(object v)
        {
            return v is double d && d >= 0 && d <= int.MaxValue ? (int?)(int)d : null;
        }
    }
}
