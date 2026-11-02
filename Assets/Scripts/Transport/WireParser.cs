using System.Collections.Generic;
using System.Globalization;
using AgentConsole.Domain;

namespace AgentConsole.Transport
{
    /// <summary>
    /// The request body for EVA's POST /submit_prompt. Only the fields EVA's PromptData needs:
    /// the rest (history, image_url, isFirstChat) keep their server-side defaults.
    /// </summary>
    public sealed class SubmitPromptRequest
    {
        public string Prompt { get; }
        public string ChatId { get; }

        public SubmitPromptRequest(string prompt, string chatId)
        {
            Prompt = prompt ?? string.Empty;
            ChatId = chatId;
        }

        public string ToJson()
        {
            string json = "{\"prompt\":" + MiniJson.Quote(Prompt);
            if (!string.IsNullOrEmpty(ChatId))
            {
                json += ",\"chat_id\":" + MiniJson.Quote(ChatId);
            }
            return json + "}";
        }
    }

    /// <summary>
    /// Parses one EVA SSE <c>data:</c> payload into an <see cref="AgentEvent"/>.
    /// <code>
    /// {"type":"meta","data":{"model":...,"temperature":...,"job_id":...}}
    /// {"type":"thought","data":"delta"}   {"type":"content","data":"delta"}
    /// {"type":"ai_callback","data":{"event":"tool_start","tool":...,"input":...,"run_id":...}}
    /// {"type":"error","data":"..."}
    /// </code>
    /// Tolerant by design (INTERFACE.md section 1): unknown fields are ignored, and a null tool or
    /// run_id is accepted, since EVA's own handler emits null when its name map misses.
    /// </summary>
    public static class WireParser
    {
        /// <returns>False for malformed JSON, an unknown type, or an unknown callback event.</returns>
        public static bool TryParse(string json, out AgentEvent ev)
        {
            ev = default;
            if (!MiniJson.TryParse(json, out object root) || !(root is Dictionary<string, object> obj))
            {
                return false;
            }

            string type = AsString(Get(obj, "type"));
            object data = Get(obj, "data");

            switch (type)
            {
                case "meta":
                {
                    Dictionary<string, object> d = data as Dictionary<string, object>;
                    float? temperature = null;
                    if (d != null && Get(d, "temperature") is double t)
                    {
                        temperature = (float)t;
                    }
                    ev = AgentEvent.Meta(AsString(Get(d, "model")), temperature, AsString(Get(d, "job_id")));
                    return true;
                }

                case "thought":
                    if (!(data is string thought)) return false;
                    ev = AgentEvent.Thought(thought);
                    return true;

                case "content":
                    if (!(data is string content)) return false;
                    ev = AgentEvent.Content(content);
                    return true;

                case "error":
                    ev = AgentEvent.Error(ErrorTextOf(data));
                    return true;

                case "ai_callback":
                    return TryParseCallback(data as Dictionary<string, object>, out ev);

                default:
                    return false;
            }
        }

        private static bool TryParseCallback(Dictionary<string, object> d, out AgentEvent ev)
        {
            ev = default;
            if (d == null) return false;

            string tool = AsString(Get(d, "tool"));
            string runId = AsString(Get(d, "run_id"));

            switch (AsString(Get(d, "event")))
            {
                // EVA also emits llm_start; the prototype treats it the same as chat_model_start.
                case "chat_model_start":
                case "llm_start":
                    ev = AgentEvent.ChatModelStart();
                    return true;
                case "tool_start":
                    ev = AgentEvent.ToolStart(tool, AsStringOrJson(Get(d, "input")), runId);
                    return true;
                case "tool_end":
                    ev = AgentEvent.ToolEnd(tool, AsStringOrJson(Get(d, "output")), runId);
                    return true;
                case "tool_error":
                    ev = AgentEvent.ToolError(tool, AsStringOrJson(Get(d, "error")), runId);
                    return true;
                case "llm_end":
                    ev = AgentEvent.LlmEnd();
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>The fatal text may be a bare string, or an object carrying message/detail.</summary>
        private static string ErrorTextOf(object data)
        {
            if (data is string s) return s;
            if (data is Dictionary<string, object> d)
            {
                string msg = AsString(Get(d, "message")) ?? AsString(Get(d, "detail"));
                if (msg != null) return msg;
            }
            return data == null ? string.Empty : MiniJson.Serialize(data);
        }

        private static object Get(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            d.TryGetValue(key, out object v);
            return v;
        }

        private static string AsString(object v)
        {
            switch (v)
            {
                case null: return null;
                case string s: return s;
                case double d: return d.ToString("R", CultureInfo.InvariantCulture);
                case bool b: return b ? "true" : "false";
                default: return null;
            }
        }

        /// <summary>A tool input may arrive as an object rather than a string; keep it as JSON text.</summary>
        private static string AsStringOrJson(object v)
        {
            if (v == null) return null;
            if (v is string s) return s;
            return MiniJson.Serialize(v);
        }
    }
}
