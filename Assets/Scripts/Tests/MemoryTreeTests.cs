using System.Collections.Generic;
using NUnit.Framework;
using AgentConsole.Domain;

namespace AgentConsole.Tests
{
    public class MemoryTreeTests
    {
        private sealed class Recorder
        {
            public readonly List<MemoryNode> Added = new List<MemoryNode>();
            public readonly List<MemoryNode> Touched = new List<MemoryNode>();
            public int Cleared;

            public Recorder(MemoryTree t)
            {
                t.NodeAdded += n => Added.Add(n);
                t.NodeTouched += n => Touched.Add(n);
                t.Cleared += () => Cleared++;
            }
        }

        [Test]
        public void StartsEmpty()
        {
            MemoryTree t = new MemoryTree();
            Assert.AreEqual(0, t.Count);
            Assert.AreEqual(0, t.NoteCount);
        }

        [Test]
        public void ATouch_BuildsRootFoldersAndANoteLeaf()
        {
            MemoryTree t = new MemoryTree();
            MemoryNode note = t.Touch("Projects/Eva AI.md", MemoryAccessKind.Read);

            Assert.AreEqual(3, t.Count);
            Assert.AreEqual(MemoryNodeKind.Root, t.Nodes[0].Kind);
            Assert.AreEqual("vault", t.Nodes[0].Label);
            Assert.AreEqual(-1, t.Nodes[0].ParentIndex);
            Assert.AreEqual(MemoryNodeKind.Folder, t.Nodes[1].Kind);
            Assert.AreEqual("Projects", t.Nodes[1].Label);
            Assert.AreEqual(0, t.Nodes[1].ParentIndex);
            Assert.AreSame(note, t.Nodes[2]);
            Assert.AreEqual(MemoryNodeKind.Note, note.Kind);
            Assert.AreEqual("Eva AI", note.Label, "the label is the file name without .md");
            Assert.AreEqual("Projects/Eva AI.md", note.Path);
            Assert.AreEqual(1, note.ParentIndex);
        }

        [Test]
        public void DeepPaths_NestFolders_AndASiblingSharesTheSameFolder()
        {
            MemoryTree t = new MemoryTree();
            t.Touch("Trading/Reviews/2026-11-02 Review.md", MemoryAccessKind.Written);
            t.Touch("Trading/Playbook.md", MemoryAccessKind.Hit);
            t.Touch("Trading/Reviews/Q3.md", MemoryAccessKind.Hit);

            // root, Trading, Reviews, Review note, Playbook, Q3: the Trading and Reviews folders exist once.
            Assert.AreEqual(6, t.Count);
            Assert.AreEqual("Reviews", t.Nodes[2].Label);
            Assert.AreEqual(1, t.Nodes[2].ParentIndex);
            Assert.AreEqual(1, t.Nodes[4].ParentIndex, "Playbook hangs from Trading");
            Assert.AreEqual(2, t.Nodes[5].ParentIndex, "Q3 hangs from Reviews");
        }

        [Test]
        public void ARootLevelFile_HangsFromTheRoot()
        {
            MemoryTree t = new MemoryTree();
            MemoryNode n = t.Touch("README.md", MemoryAccessKind.Read);
            Assert.AreEqual(2, t.Count);
            Assert.AreEqual(0, n.ParentIndex);
            Assert.AreEqual("README", n.Label);
        }

        [Test]
        public void TheSamePathTwice_NeverDuplicatesNodes()
        {
            MemoryTree t = new MemoryTree();
            MemoryNode a = t.Touch("A/B/c.md", MemoryAccessKind.Hit);
            MemoryNode b = t.Touch("A/B/c.md", MemoryAccessKind.Hit);
            MemoryNode c = t.Touch("./A//B\\c.md", MemoryAccessKind.Hit);
            Assert.AreSame(a, b);
            Assert.AreSame(a, c);
            Assert.AreEqual(4, t.Count);
            Assert.AreEqual(1, t.NoteCount);
        }

        [Test]
        public void AFolderAndANoteMayShareAName()
        {
            MemoryTree t = new MemoryTree();
            t.Touch("Eva/eva.md", MemoryAccessKind.Hit);
            t.Touch("Eva", MemoryAccessKind.Hit); // a root-level file called "Eva"
            Assert.AreEqual(4, t.Count);
            Assert.AreEqual(2, t.NoteCount);
        }

        [Test]
        public void State_OnlyEverUpgrades()
        {
            MemoryTree t = new MemoryTree();
            MemoryNode n = t.Touch("A/n.md", MemoryAccessKind.Hit);
            Assert.AreEqual(MemoryAccessKind.Hit, n.State);
            t.Touch("A/n.md", MemoryAccessKind.Read);
            Assert.AreEqual(MemoryAccessKind.Read, n.State);
            t.Touch("A/n.md", MemoryAccessKind.Hit);
            Assert.AreEqual(MemoryAccessKind.Read, n.State, "a later search hit does not downgrade a read");
            t.Touch("A/n.md", MemoryAccessKind.Written);
            Assert.AreEqual(MemoryAccessKind.Written, n.State);
            t.Touch("A/n.md", MemoryAccessKind.Read);
            t.Touch("A/n.md", MemoryAccessKind.Hit);
            Assert.AreEqual(MemoryAccessKind.Written, n.State, "never back down");
        }

        [Test]
        public void AccessCount_CountsEveryTouch_AndOrderIsFirstAppearance()
        {
            MemoryTree t = new MemoryTree();
            MemoryNode a = t.Touch("A/a.md", MemoryAccessKind.Hit);
            MemoryNode b = t.Touch("B/b.md", MemoryAccessKind.Hit);
            t.Touch("A/a.md", MemoryAccessKind.Read);
            t.Touch("A/a.md", MemoryAccessKind.Hit);
            Assert.AreEqual(3, a.AccessCount);
            Assert.AreEqual(1, b.AccessCount);
            Assert.Less(a.Order, b.Order);
            Assert.AreEqual(0, t.Nodes[1].AccessCount, "a folder is not a memory");
        }

        [Test]
        public void Events_AreRaisedOncePerNodeAndOncePerTouch_ParentsFirst()
        {
            MemoryTree t = new MemoryTree();
            Recorder r = new Recorder(t);

            t.Touch("Projects/Eva AI.md", MemoryAccessKind.Read);
            Assert.AreEqual(3, r.Added.Count);
            Assert.AreEqual(MemoryNodeKind.Root, r.Added[0].Kind);
            Assert.AreEqual(MemoryNodeKind.Folder, r.Added[1].Kind);
            Assert.AreEqual(MemoryNodeKind.Note, r.Added[2].Kind);
            Assert.AreEqual(1, r.Touched.Count);
            Assert.AreSame(r.Added[2], r.Touched[0]);

            t.Touch("Projects/Eva AI.md", MemoryAccessKind.Written);
            Assert.AreEqual(3, r.Added.Count, "nothing new was added");
            Assert.AreEqual(2, r.Touched.Count);
            Assert.AreEqual(MemoryAccessKind.Written, r.Touched[1].State, "the state is updated before the event");

            t.Touch("Projects/Other.md", MemoryAccessKind.Hit);
            Assert.AreEqual(4, r.Added.Count, "only the new note");
            Assert.AreEqual(3, r.Touched.Count);
        }

        [Test]
        public void ANoteIsCompleteWhenItsAddedEventFires()
        {
            MemoryTree t = new MemoryTree();
            MemoryAccessKind seen = MemoryAccessKind.Hit;
            int count = -1;
            t.NodeAdded += n =>
            {
                if (n.Kind != MemoryNodeKind.Note) return;
                seen = n.State;
                count = n.AccessCount;
            };
            t.Touch("A/n.md", MemoryAccessKind.Written);
            Assert.AreEqual(MemoryAccessKind.Written, seen);
            Assert.AreEqual(1, count);
        }

        [Test]
        public void Clear_EmptiesTheTree_RaisesOnce_AndAllowsAFreshStart()
        {
            MemoryTree t = new MemoryTree();
            Recorder r = new Recorder(t);
            t.Touch("A/a.md", MemoryAccessKind.Written);
            t.Clear();
            Assert.AreEqual(0, t.Count);
            Assert.AreEqual(0, t.NoteCount);
            Assert.AreEqual(1, r.Cleared);

            t.Clear();
            Assert.AreEqual(1, r.Cleared, "clearing an empty tree raises nothing");

            MemoryNode again = t.Touch("A/a.md", MemoryAccessKind.Hit);
            Assert.AreEqual(MemoryAccessKind.Hit, again.State, "the old Written state did not survive the clear");
            Assert.AreEqual(1, again.AccessCount);
            Assert.AreEqual(3, t.Count);
            Assert.AreEqual(0, t.Nodes[0].Index);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("/")]
        [TestCase(" / . / ")]
        public void AnEmptyPath_IsIgnored(string path)
        {
            MemoryTree t = new MemoryTree();
            Recorder r = new Recorder(t);
            Assert.IsNull(t.Touch(path, MemoryAccessKind.Read));
            Assert.AreEqual(0, t.Count);
            Assert.AreEqual(0, r.Added.Count);
            Assert.AreEqual(0, r.Touched.Count);
        }

        [Test]
        public void TryFindNote_FindsATouchedNoteByAnyPathSpelling()
        {
            MemoryTree t = new MemoryTree();
            t.Touch("Projects/Eva AI.md", MemoryAccessKind.Hit);
            Assert.IsTrue(t.TryFindNote("Projects/Eva AI.md", out MemoryNode n));
            Assert.AreEqual("Eva AI", n.Label);
            Assert.IsTrue(t.TryFindNote("./Projects\\Eva AI.md", out MemoryNode m));
            Assert.AreSame(n, m);
            Assert.IsFalse(t.TryFindNote("Projects/Nope.md", out _));
            Assert.IsFalse(t.TryFindNote("Projects", out _), "a folder is not a note");
            Assert.IsFalse(t.TryFindNote(null, out _));
        }

        [Test]
        public void ParentsAlwaysPrecedeChildren()
        {
            MemoryTree t = new MemoryTree();
            string[] paths = { "a/b/c/d.md", "x.md", "a/e.md", "a/b/f.md", "z/y/w.md" };
            foreach (string p in paths) t.Touch(p, MemoryAccessKind.Hit);
            for (int i = 1; i < t.Count; i++)
            {
                Assert.Less(t.Nodes[i].ParentIndex, i);
                Assert.GreaterOrEqual(t.Nodes[i].ParentIndex, 0);
                Assert.AreEqual(i, t.Nodes[i].Index);
            }
        }

        // ---- layout

        [Test]
        public void Layout_RootIsOnTheAxis_ChildrenFanOutBySubtreeLeaves()
        {
            MemoryTree t = new MemoryTree();
            // Projects has two notes, Eva has one: three leaves, so three slots, and the folders sit over their own leaves.
            t.Touch("Projects/A.md", MemoryAccessKind.Hit);
            t.Touch("Projects/B.md", MemoryAccessKind.Hit);
            t.Touch("Eva/eva.md", MemoryAccessKind.Hit);
            MemoryTreeLayout l = new MemoryTreeLayout();
            l.Compute(t);

            Assert.AreEqual(t.Count, l.Count);
            Assert.AreEqual(0f, l.LateralOf(0), 1e-5f);
            Assert.AreEqual(0, l.DepthOf(0));
            Assert.AreEqual(1, l.DepthOf(1));
            Assert.AreEqual(2, l.DepthOf(2));
            Assert.AreEqual(2, l.MaxDepth);
            Assert.AreEqual(3, l.LeavesOf(0));
            Assert.AreEqual(2, l.LeavesOf(1));

            // Leaves are one slot apart, in first-appearance order: A, B, eva.
            Assert.AreEqual(1f, l.LateralOf(3) - l.LateralOf(2), 1e-5f);
            Assert.AreEqual(1f, l.LateralOf(5) - l.LateralOf(3), 1e-5f);
            // A parent is centred over its children.
            Assert.AreEqual((l.LateralOf(2) + l.LateralOf(3)) * 0.5f, l.LateralOf(1), 1e-5f);
            // The tree is centred: the leaves straddle the root's axis.
            Assert.Less(l.LateralOf(2), 0f);
            Assert.Greater(l.LateralOf(5), 0f);
        }

        [Test]
        public void Layout_SiblingBranchesNeverOverlap_AndAWideBranchGetsMoreRoom()
        {
            MemoryTree t = new MemoryTree();
            for (int i = 0; i < 5; i++) t.Touch("Wide/n" + i + ".md", MemoryAccessKind.Hit);
            t.Touch("Narrow/only.md", MemoryAccessKind.Hit);
            MemoryTreeLayout l = new MemoryTreeLayout();
            l.Compute(t);

            int wide = 1;
            int narrow = 7;
            Assert.AreEqual("Wide", t.Nodes[wide].Label);
            Assert.AreEqual("Narrow", t.Nodes[narrow].Label);
            Assert.AreEqual(5, l.LeavesOf(wide));
            Assert.AreEqual(1, l.LeavesOf(narrow));
            // The narrow folder's slot starts after the wide branch's last leaf.
            float lastWide = l.LateralOf(6);
            Assert.GreaterOrEqual(l.LateralOf(narrow) - lastWide, 1f - 1e-5f);

            // No two nodes at the same depth share a slot.
            for (int a = 0; a < t.Count; a++)
            {
                for (int b = a + 1; b < t.Count; b++)
                {
                    if (l.DepthOf(a) != l.DepthOf(b)) continue;
                    Assert.GreaterOrEqual(System.Math.Abs(l.LateralOf(a) - l.LateralOf(b)), 1f - 1e-5f, a + " vs " + b);
                }
            }
        }

        [Test]
        public void Layout_IsDeterministic_AndReusableAcrossGrowthAndClear()
        {
            MemoryTree t = new MemoryTree();
            MemoryTreeLayout l = new MemoryTreeLayout();
            l.Compute(t);
            Assert.AreEqual(0, l.Count);

            for (int i = 0; i < 40; i++) t.Touch("F" + (i % 7) + "/n" + i + ".md", MemoryAccessKind.Hit);
            l.Compute(t);
            float[] first = new float[t.Count];
            for (int i = 0; i < t.Count; i++) first[i] = l.LateralOf(i);

            MemoryTreeLayout other = new MemoryTreeLayout();
            other.Compute(t);
            for (int i = 0; i < t.Count; i++) Assert.AreEqual(first[i], other.LateralOf(i), 1e-6f);

            t.Clear();
            t.Touch("Solo/one.md", MemoryAccessKind.Hit);
            l.Compute(t);
            Assert.AreEqual(3, l.Count);
            Assert.AreEqual(0f, l.LateralOf(0), 1e-6f);
        }
    }
}
