using System;
using UnityEngine;

namespace AgentConsole.View
{
    [Serializable]
    public sealed class LayoutSettings
    {
        [Header("Shells (tier radii)")]
        public float ServerShell = 10.5f;
        public float ToolShell = 20.0f;

        [Header("Forces")]
        [Tooltip("Pair repulsion = Repulsion * (ri + rj)^2 / d^2, so big spheres claim more room.")]
        public float Repulsion = 78f;
        public float CollisionPadding = 1.8f;
        public float CollisionStrength = 40f;
        public float LinkStiffness = 5f;
        public float ShellStiffness = 10f;

        [Header("Integration")]
        [Range(0.5f, 0.99f)] public float VelocityRetention = 0.88f;
        public float MaxSpeed = 14f;
        public float Tick = 1f / 60f;

        [Header("Cooling (settle, then freeze)")]
        public float AlphaDecay = 0.014f;
        public float AlphaMin = 0.003f;
        [Tooltip("Freeze early once the fastest node is slower than this (units/s) for 30 ticks.")]
        public float RestSpeed = 0.03f;
    }

    /// <summary>
    /// 3D force layout over arrays, no allocation in Step(). Spring links plus pairwise repulsion that
    /// scales with sphere radius, plus a soft radial spring to each tier's shell. It cools like d3's
    /// alpha and then FREEZES: Step() returns false and nothing moves until <see cref="Heat"/> is called
    /// again (a node was grafted). A substrate that drifts forever is noise on a monitor and nausea in
    /// a headset.
    /// </summary>
    public sealed class ForceLayout3D
    {
        private const float GoldenAngle = 2.39996323f;

        private readonly LayoutSettings _s;
        private Vector3[] _pos = new Vector3[64];
        private Vector3[] _vel = new Vector3[64];
        private Vector3[] _force = new Vector3[64];
        private float[] _rad = new float[64];
        private int[] _tier = new int[64];
        private bool[] _pinned = new bool[64];
        private int _n;

        private int[] _la = new int[128];
        private int[] _lb = new int[128];
        private float[] _lrest = new float[128];
        private int _m;

        private float _alpha;
        private int _restTicks;

        public ForceLayout3D(LayoutSettings settings)
        {
            _s = settings ?? new LayoutSettings();
        }

        public int NodeCount => _n;
        public int LinkCount => _m;
        public float Alpha => _alpha;
        public bool IsSettled => _alpha <= _s.AlphaMin;
        public Vector3 PositionOf(int i) => _pos[i];
        public float RadiusOf(int i) => _rad[i];

        public void Clear()
        {
            _n = 0;
            _m = 0;
            _alpha = 0f;
            _restTicks = 0;
        }

        /// <summary>A point on the unit sphere. Fibonacci spiral: even coverage with no clumping.</summary>
        public static Vector3 Fibonacci(float k, float n)
        {
            if (n < 1f) n = 1f;
            float y = 1f - 2f * (k + 0.5f) / n;
            float r = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
            float theta = k * GoldenAngle;
            return new Vector3(Mathf.Cos(theta) * r, y, Mathf.Sin(theta) * r);
        }

        public int AddNode(int tier, float radius, Vector3 seed, bool pinned)
        {
            if (_n == _pos.Length)
            {
                int cap = _n * 2;
                Array.Resize(ref _pos, cap);
                Array.Resize(ref _vel, cap);
                Array.Resize(ref _force, cap);
                Array.Resize(ref _rad, cap);
                Array.Resize(ref _tier, cap);
                Array.Resize(ref _pinned, cap);
            }
            _pos[_n] = seed;
            _vel[_n] = Vector3.zero;
            _rad[_n] = radius;
            _tier[_n] = tier;
            _pinned[_n] = pinned;
            return _n++;
        }

        public void AddLink(int a, int b, float rest)
        {
            if (_m == _la.Length)
            {
                int cap = _m * 2;
                Array.Resize(ref _la, cap);
                Array.Resize(ref _lb, cap);
                Array.Resize(ref _lrest, cap);
            }
            _la[_m] = a;
            _lb[_m] = b;
            _lrest[_m] = rest;
            _m++;
        }

        /// <summary>Re-heat after a graft. Only ever raises alpha.</summary>
        public void Heat(float alpha)
        {
            if (alpha > _alpha) _alpha = alpha;
            _restTicks = 0;
        }

/// <summary>Pinned nodes ignore forces and hold their position. A drag pins the grabbed node.</summary>
        public void SetPinned(int i, bool pinned)
        {
            if (i < 0 || i >= _n) return;
            _pinned[i] = pinned;
            _vel[i] = Vector3.zero;
            _restTicks = 0;
        }

        public bool IsPinned(int i) => i >= 0 && i < _n && _pinned[i];

        /// <summary>Move a node directly (a drag). Its velocity is dropped so it does not carry stale momentum on release.</summary>
        public void SetPosition(int i, Vector3 p)
        {
            if (i < 0 || i >= _n) return;
            _pos[i] = p;
            _vel[i] = Vector3.zero;
        }


        /// <summary>One fixed tick. Returns false (and does nothing) once settled.</summary>
        public bool Step()
        {
            if (_alpha <= _s.AlphaMin || _n == 0) return false;

            float dt = _s.Tick;
            for (int i = 0; i < _n; i++) _force[i] = Vector3.zero;

            for (int i = 0; i < _n; i++)
            {
                for (int j = i + 1; j < _n; j++)
                {
                    Vector3 d = _pos[i] - _pos[j];
                    float d2 = d.sqrMagnitude;
                    if (d2 < 1e-6f)
                    {
                        d = new Vector3(0.011f * (i - j), 0.013f, 0.007f);
                        d2 = d.sqrMagnitude;
                    }
                    float dist = Mathf.Sqrt(d2);
                    float rs = _rad[i] + _rad[j];
                    float f = _s.Repulsion * rs * rs / d2;
                    float minD = rs + _s.CollisionPadding;
                    if (dist < minD) f += (minD - dist) * _s.CollisionStrength;
                    Vector3 fv = d * (f / dist);
                    _force[i] += fv;
                    _force[j] -= fv;
                }
            }

            for (int k = 0; k < _m; k++)
            {
                int a = _la[k];
                int b = _lb[k];
                Vector3 d = _pos[b] - _pos[a];
                float dist = d.magnitude;
                if (dist < 1e-5f) continue;
                Vector3 fv = d * ((dist - _lrest[k]) * _s.LinkStiffness / dist);
                _force[a] += fv;
                _force[b] -= fv;
            }

            for (int i = 0; i < _n; i++)
            {
                if (_pinned[i] || _tier[i] == 0) continue;
                float target = _tier[i] == 1 ? _s.ServerShell : _s.ToolShell;
                float r = _pos[i].magnitude;
                if (r < 1e-4f) continue;
                _force[i] += _pos[i] * ((target - r) * _s.ShellStiffness / r);
            }

            float maxSpeed = 0f;
            for (int i = 0; i < _n; i++)
            {
                if (_pinned[i])
                {
                    _vel[i] = Vector3.zero;
                    continue;
                }
                Vector3 v = (_vel[i] + _force[i] * (_alpha * dt)) * _s.VelocityRetention;
                float sp = v.magnitude;
                if (sp > _s.MaxSpeed)
                {
                    v *= _s.MaxSpeed / sp;
                    sp = _s.MaxSpeed;
                }
                _vel[i] = v;
                _pos[i] += v * dt;
                if (sp > maxSpeed) maxSpeed = sp;
            }

            _alpha -= _alpha * _s.AlphaDecay;

            // Converged early: the cooling schedule is an upper bound, not a minimum.
            if (maxSpeed < _s.RestSpeed && _alpha < 0.5f)
            {
                if (++_restTicks >= 30) _alpha = 0f;
            }
            else
            {
                _restTicks = 0;
            }
            return true;
        }
    }
}
