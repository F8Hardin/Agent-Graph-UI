using System;

namespace AgentConsole.Transport
{
    /// <summary>Reconnect and timeout settings for a streaming transport.</summary>
    public sealed class ReconnectPolicy
    {
        public int InitialDelayMs = 500;
        public int MaxDelayMs = 15000;
        public double Factor = 2.0;

        /// <summary>Fraction of the delay randomised in either direction, 0 for none.</summary>
        public double Jitter = 0.2;

        /// <summary>Consecutive failed reconnects before giving up. The counter resets whenever a connection makes progress.</summary>
        public int MaxAttempts = 8;

        /// <summary>Time allowed to get response headers.</summary>
        public int ConnectTimeoutMs = 15000;

        /// <summary>
        /// Time allowed for GET /models, headers and body. Generous: the inference server may be asked to load
        /// something before it answers.
        /// </summary>
        public int ModelListTimeoutMs = 60000;

        /// <summary>
        /// Time allowed for POST /set-model, which does not answer until the new model is resident. A 27B model
        /// takes tens of seconds, and EVA retries a failed load internally, so this is minutes, not seconds.
        /// </summary>
        public int ModelSwitchTimeoutMs = 300000;

        /// <summary>
        /// Longest silence before a stream counts as dead. EVA sends a keepalive every 20 seconds,
        /// so a healthy stream is never quiet this long.
        /// </summary>
        public int IdleTimeoutMs = 60000;

        /// <summary>Delay before reconnect number <paramref name="attempt"/> (1-based). Deterministic when rng is null.</summary>
        public TimeSpan DelayFor(int attempt, Random rng = null)
        {
            if (attempt < 1) attempt = 1;
            double ms = InitialDelayMs * Math.Pow(Factor, attempt - 1);
            if (ms > MaxDelayMs || double.IsInfinity(ms) || double.IsNaN(ms)) ms = MaxDelayMs;
            if (rng != null && Jitter > 0)
            {
                ms *= 1.0 + (rng.NextDouble() * 2.0 - 1.0) * Jitter;
            }
            if (ms < 0) ms = 0;
            return TimeSpan.FromMilliseconds(ms);
        }
    }
}
