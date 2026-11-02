using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace AgentConsole.View
{
    /// <summary>
    /// Every edge in ONE procedural mesh and ONE draw call (see the AgentConsole/Edge shader for the
    /// vertex layout). Geometry is rebuilt only when the layout moves. Pulses use a fixed, preallocated
    /// pool: a pulse is a head position travelling along one edge, forward or reversed, and the pool
    /// writes the heads into a vertex stream that the shader draws as a bright bead. A pool beats a
    /// scrolling-UV shader here because pulses start and stop on arbitrary tool events and the return
    /// trip must run the other way; a scrolling UV is a loop with a fixed direction. Nothing allocates.
    /// </summary>
    internal sealed class EdgeMesh
    {
        private const int PoolSize = 48;

        private struct Pulse
        {
            public bool Active;
            public int Edge;
            public float T;
            public bool Reverse;
        }

        private readonly Mesh _mesh;
        private readonly GameObject _go;
        private readonly Pulse[] _pool = new Pulse[PoolSize];
        private int _next;

        private Vector3[] _pos = new Vector3[0];
        private Vector3[] _dir = new Vector3[0];
        private Vector2[] _uv0 = new Vector2[0];
        private Vector2[] _uv1 = new Vector2[0];
        private Vector4[] _pulse = new Vector4[0];
        private int[] _tris = new int[0];
        private float[] _hot = new float[0];
        private float[] _h0 = new float[0];
        private float[] _h1 = new float[0];
        private bool[] _mem = new bool[0];
        private int _cap;
        private int _count;
        private bool _structureDirty;

        public EdgeMesh(Transform parent, Material material)
        {
            _mesh = new Mesh { name = "AgentConsole Edges" };
            _mesh.MarkDynamic();
            _mesh.indexFormat = IndexFormat.UInt32;
            _go = new GameObject("Edges");
            _go.transform.SetParent(parent, false);
            _go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            MeshRenderer mr = _go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        public float PacketSeconds { get; set; } = 0.5f;
        public int Count => _count;
        public Material Material => _go.GetComponent<MeshRenderer>().sharedMaterial;

        public void SetCount(int n)
        {
            if (n > _cap) Grow(Math.Max(n, Math.Max(16, _cap * 2)));
            if (n != _count)
            {
                _count = n;
                _structureDirty = true;
            }
        }

        private void Grow(int cap)
        {
            int v = cap * 4;
            int oldCap = _cap;
            Array.Resize(ref _pos, v);
            Array.Resize(ref _dir, v);
            Array.Resize(ref _uv0, v);
            Array.Resize(ref _uv1, v);
            Array.Resize(ref _pulse, v);
            Array.Resize(ref _tris, cap * 6);
            Array.Resize(ref _hot, cap);
            Array.Resize(ref _h0, cap);
            Array.Resize(ref _h1, cap);
            Array.Resize(ref _mem, cap);
            for (int e = 0; e < cap; e++)
            {
                int b = e * 4;
                if (e >= oldCap)
                {
                    // No pulse head yet: heads < 0 mean "none" to the shader.
                    Vector4 idle = new Vector4(0f, -1f, -1f, 0f);
                    _pulse[b + 0] = idle;
                    _pulse[b + 1] = idle;
                    _pulse[b + 2] = idle;
                    _pulse[b + 3] = idle;
                    _h0[e] = -1f;
                    _h1[e] = -1f;
                }
                _uv0[b + 0] = new Vector2(0f, -1f);
                _uv0[b + 1] = new Vector2(0f, 1f);
                _uv0[b + 2] = new Vector2(1f, -1f);
                _uv0[b + 3] = new Vector2(1f, 1f);
                int t = e * 6;
                _tris[t + 0] = b + 0;
                _tris[t + 1] = b + 1;
                _tris[t + 2] = b + 2;
                _tris[t + 3] = b + 2;
                _tris[t + 4] = b + 1;
                _tris[t + 5] = b + 3;
            }
            _cap = cap;
            _structureDirty = true;
        }

        /// <summary>Set one edge from endpoint a to b (already on the sphere surfaces, object space).</summary>
        public void SetEdge(int i, Vector3 a, Vector3 b, int tier, bool down)
        {
            Vector3 d = b - a;
            float len = d.magnitude;
            if (len < 1e-4f)
            {
                d = Vector3.up * 1e-3f;
                len = 1e-3f;
            }
            Vector3 n = d / len;
            int v = i * 4;
            _pos[v + 0] = a;
            _pos[v + 1] = a;
            _pos[v + 2] = b;
            _pos[v + 3] = b;
            float tierCode = tier + (down ? 0.5f : 0f);
            Vector2 u1 = new Vector2(tierCode, len);
            for (int k = 0; k < 4; k++)
            {
                _dir[v + k] = n * len;
                _uv1[v + k] = u1;
            }
        }

        /// <summary>Upload geometry after SetEdge calls. Cheap; called once per layout tick, then never again once settled.</summary>
        public void CommitGeometry()
        {
            int vc = _count * 4;
            if (_structureDirty)
            {
                _mesh.Clear();
                _structureDirty = false;
            }
            _mesh.SetVertices(_pos, 0, vc);
            _mesh.SetNormals(_dir, 0, vc);
            _mesh.SetUVs(0, _uv0, 0, vc);
            _mesh.SetUVs(1, _uv1, 0, vc);
            _mesh.SetUVs(2, _pulse, 0, vc);
            _mesh.SetTriangles(_tris, 0, _count * 6, 0, false);
            _mesh.bounds = new Bounds(Vector3.zero, new Vector3(400f, 400f, 400f));
        }

        public void SetHot(int i, float hot) => _hot[i] = hot;
        public void SetMemory(int i, bool mem) => _mem[i] = mem;

        /// <summary>Start a pulse on an edge. Reverse runs target -> source (the return trip).</summary>
        public void Emit(int edge, bool reverse)
        {
            if (edge < 0 || edge >= _count) return;
            int slot = -1;
            for (int i = 0; i < PoolSize; i++)
            {
                if (!_pool[i].Active)
                {
                    slot = i;
                    break;
                }
            }
            if (slot < 0)
            {
                slot = _next;
                _next = (_next + 1) % PoolSize;
            }
            _pool[slot] = new Pulse { Active = true, Edge = edge, T = 0f, Reverse = reverse };
        }

        /// <summary>Advance pulses. Returns true if any is still in flight.</summary>
        public bool Advance(float dt)
        {
            bool any = false;
            float step = dt / Mathf.Max(0.05f, PacketSeconds);
            for (int i = 0; i < PoolSize; i++)
            {
                if (!_pool[i].Active) continue;
                _pool[i].T += step;
                if (_pool[i].T >= 1f)
                {
                    _pool[i].Active = false;
                    continue;
                }
                any = true;
            }
            return any;
        }

        /// <summary>Write hot + pulse heads into the vertex stream and upload it. Called only while something is lit.</summary>
        public void UploadPulses()
        {
            for (int e = 0; e < _count; e++)
            {
                _h0[e] = -1f;
                _h1[e] = -1f;
            }
            for (int i = 0; i < PoolSize; i++)
            {
                if (!_pool[i].Active) continue;
                int e = _pool[i].Edge;
                if (e >= _count) continue;
                float head = _pool[i].Reverse ? 1f - _pool[i].T : _pool[i].T;
                if (_h0[e] < 0f) _h0[e] = head;
                else _h1[e] = head;
            }
            for (int e = 0; e < _count; e++)
            {
                Vector4 p = new Vector4(_hot[e], _h0[e], _h1[e], _mem[e] ? 1f : 0f);
                int v = e * 4;
                _pulse[v + 0] = p;
                _pulse[v + 1] = p;
                _pulse[v + 2] = p;
                _pulse[v + 3] = p;
            }
            _mesh.SetUVs(2, _pulse, 0, _count * 4);
        }

        public void Destroy()
        {
            if (_mesh != null) UnityEngine.Object.Destroy(_mesh);
            if (_go != null) UnityEngine.Object.Destroy(_go);
        }
    }
}
