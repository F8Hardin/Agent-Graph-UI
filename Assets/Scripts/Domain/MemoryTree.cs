using System;
using System.Collections.Generic;

namespace AgentConsole.Domain
{
    public enum MemoryNodeKind
    {
        Root,
        Folder,
        Note
    }

    /// <summary>
    /// One node of the session memory tree. Written only by <see cref="MemoryTree"/>; a view reads it. Parents always come
    /// before their children in the tree's list, so a bottom-up or top-down pass is a plain loop over the indices.
    /// </summary>
    public sealed class MemoryNode
    {
        public int Index { get; }

        /// <summary>Index of the parent, or -1 for the root.</summary>
        public int ParentIndex { get; }

        public MemoryNodeKind Kind { get; }

        /// <summary>The folder name, or the file name without its ".md". The root is "vault".</summary>
        public string Label { get; }

        /// <summary>Full vault path with forward slashes: "Projects/Eva AI.md" for a note, "Projects" for a folder, "" for the root.</summary>
        public string Path { get; }

        /// <summary>How strongly this note was touched. Only ever upgrades (Hit, Read, Written). Folders and the root stay Hit.</summary>
        public MemoryAccessKind State { get; internal set; }

        /// <summary>How many times a call touched this note. Folders and the root stay 0.</summary>
        public int AccessCount { get; internal set; }

        /// <summary>The order of first appearance: the same number as <see cref="Index"/>, named for what it means.</summary>
        public int Order => Index;

        internal MemoryNode(int index, int parentIndex, MemoryNodeKind kind, string label, string path)
        {
            Index = index;
            ParentIndex = parentIndex;
            Kind = kind;
            Label = label;
            Path = path;
        }
    }

    /// <summary>
    /// The memories EVA touched in this session, as a tree that starts empty and grows. Built from the files successful
    /// memory tool calls reported (see <see cref="MemoryAccessParser"/>), never from the vault itself: the user's decision
    /// was a growing tree of what was accessed, not a copy of the whole Obsidian vault. Pure model: no engine types, plain
    /// C# events, main-thread only.
    /// </summary>
    public sealed class MemoryTree
    {
        public const string RootLabel = "vault";

        private readonly List<MemoryNode> _nodes = new List<MemoryNode>();
        private readonly Dictionary<string, int> _folders = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _notes = new Dictionary<string, int>(StringComparer.Ordinal);

        public IReadOnlyList<MemoryNode> Nodes => _nodes;
        public int Count => _nodes.Count;

        /// <summary>Notes (leaves) only: what a user would call "memories accessed".</summary>
        public int NoteCount => _notes.Count;

        /// <summary>A node was created. Raised for the root, each folder and the note, parents first, before <see cref="NodeTouched"/>.</summary>
        public event Action<MemoryNode> NodeAdded;

        /// <summary>A note was touched: raised once per touch, new or existing, after its state and count are updated.</summary>
        public event Action<MemoryNode> NodeTouched;

        /// <summary>The tree was emptied (a new session). Not raised when it was already empty.</summary>
        public event Action Cleared;

        /// <summary>
        /// Record that a note was touched, creating the root and folders it needs. Returns the note, or null if the path
        /// has no usable segment. The state only upgrades: a Read after a Written leaves it Written (the access still counts).
        /// </summary>
        public MemoryNode Touch(string path, MemoryAccessKind kind)
        {
            string[] parts = Segments(path);
            if (parts.Length == 0) return null;

            if (_nodes.Count == 0) Add(-1, MemoryNodeKind.Root, RootLabel, string.Empty);

            int parent = 0;
            string key = string.Empty;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                key = key.Length == 0 ? parts[i] : key + "/" + parts[i];
                if (!_folders.TryGetValue(key, out int f))
                {
                    f = Add(parent, MemoryNodeKind.Folder, parts[i], key);
                    _folders[key] = f;
                }
                parent = f;
            }

            string last = parts[parts.Length - 1];
            key = key.Length == 0 ? last : key + "/" + last;
            MemoryNode note;
            if (_notes.TryGetValue(key, out int existing))
            {
                note = _nodes[existing];
                if (kind > note.State) note.State = kind;
                note.AccessCount++;
            }
            else
            {
                int idx = Add(parent, MemoryNodeKind.Note, NoteLabel(last), key);
                _notes[key] = idx;
                note = _nodes[idx];
                note.State = kind;
                note.AccessCount = 1;
                NodeAdded?.Invoke(note);
            }
            NodeTouched?.Invoke(note);
            return note;
        }

        /// <summary>Find a note by its path (any separator style). False if it was never touched.</summary>
        public bool TryFindNote(string path, out MemoryNode note)
        {
            note = null;
            string[] parts = Segments(path);
            if (parts.Length == 0) return false;
            if (!_notes.TryGetValue(string.Join("/", parts), out int idx)) return false;
            note = _nodes[idx];
            return true;
        }

        /// <summary>Empty the tree for a new session.</summary>
        public void Clear()
        {
            if (_nodes.Count == 0) return;
            _nodes.Clear();
            _folders.Clear();
            _notes.Clear();
            Cleared?.Invoke();
        }

        // Root and folders raise NodeAdded here; a note raises it from Touch, after its state is set, so a subscriber never sees a note without one.
        private int Add(int parent, MemoryNodeKind kind, string label, string path)
        {
            MemoryNode n = new MemoryNode(_nodes.Count, parent, kind, label, path);
            _nodes.Add(n);
            if (kind != MemoryNodeKind.Note) NodeAdded?.Invoke(n);
            return n.Index;
        }

        private static string[] Segments(string path)
        {
            if (string.IsNullOrEmpty(path)) return new string[0];
            string[] raw = path.Replace('\\', '/').Split('/');
            List<string> parts = new List<string>(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                string s = raw[i].Trim();
                if (s.Length > 0 && s != ".") parts.Add(s);
            }
            return parts.ToArray();
        }

        private static string NoteLabel(string file)
        {
            const string Ext = ".md";
            return file.Length > Ext.Length && file.EndsWith(Ext, StringComparison.OrdinalIgnoreCase)
                ? file.Substring(0, file.Length - Ext.Length)
                : file;
        }
    }
}
