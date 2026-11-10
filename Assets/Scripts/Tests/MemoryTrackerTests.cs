using System.Collections.Generic;
using NUnit.Framework;
using AgentConsole.Domain;
using AgentConsole.Transport;

namespace AgentConsole.Tests
{
    public class MemoryTrackerTests
    {
        private long _now;

        private ConsoleReducer NewReducer()
        {
            _now = 0;
            return new ConsoleReducer(new GraphModel("EVA", SampleInventory.Eva()), () => _now);
        }

        private static string Q(string s) => s.Replace('`', '"');

        private const string ReadTool = "memory__vault_read";

        private static string ReadOut(string path) =>
            Q("content=[{'type': 'text', 'text': '{`path`: `" + path + "`, `title`: `t`, `frontmatter`: {`type`: `moc`}}'}]");

        // ---- the reducer's additive hooks

        [Test]
        public void ToolStartedAndToolResolved_CarryTheCall_OncePerCall()
        {
            ConsoleReducer r = NewReducer();
            List<ToolCall> started = new List<ToolCall>();
            List<ToolCall> resolved = new List<ToolCall>();
            r.ToolStarted += c => started.Add(c);
            r.ToolResolved += c => resolved.Add(c);

            r.Apply(AgentEvent.ToolStart(ReadTool, "{\"path\": \"A/b.md\"}", "a"));
            r.Apply(AgentEvent.ToolStart(ReadTool, "{\"path\": \"A/b.md\"}", "a")); // a replayed start
            Assert.AreEqual(1, started.Count);
            Assert.AreEqual("{\"path\": \"A/b.md\"}", started[0].Input);
            Assert.AreEqual(ToolStatus.Running, started[0].Status);
            Assert.AreEqual("memory", started[0].Server);

            r.Apply(AgentEvent.ToolEnd(ReadTool, "out", "a"));
            r.Apply(AgentEvent.ToolEnd(ReadTool, "out again", "a")); // a duplicate end
            Assert.AreEqual(1, resolved.Count);
            Assert.AreSame(started[0], resolved[0]);
            Assert.AreEqual(ToolStatus.Done, resolved[0].Status);
            Assert.AreEqual("out", resolved[0].Output);
        }

        [Test]
        public void ToolResolved_FiresForAnErrorWithTheErrorStatus_AndForAnEndWithNoStart()
        {
            ConsoleReducer r = NewReducer();
            List<ToolCall> resolved = new List<ToolCall>();
            r.ToolResolved += c => resolved.Add(c);

            r.Apply(AgentEvent.ToolStart(ReadTool, "{}", "a"));
            r.Apply(AgentEvent.ToolError(ReadTool, "boom", "a"));
            Assert.AreEqual(ToolStatus.Error, resolved[0].Status);

            r.Apply(AgentEvent.ToolEnd(ReadTool, "joined mid-run", "orphan"));
            Assert.AreEqual(2, resolved.Count);
            Assert.IsNull(resolved[1].Input);
            Assert.AreEqual("joined mid-run", resolved[1].Output);
        }

        [Test]
        public void ExistingToolEvents_StillFireInTheSameOrder_AndAtTheSameCounts()
        {
            ConsoleReducer r = NewReducer();
            List<string> seen = new List<string>();
            r.NodeFired += id => seen.Add("fired");
            r.NodeResolved += id => seen.Add("resolved");
            r.ToolsChanged += () => seen.Add("tools");
            r.ToolStarted += c => seen.Add("started");
            r.ToolResolved += c => seen.Add("done");
            r.Apply(AgentEvent.ToolStart(ReadTool, "{}", "a"));
            r.Apply(AgentEvent.ToolEnd(ReadTool, "x", "a"));
            // The new events sit between the old one and ToolsChanged and add nothing to either.
            Assert.AreEqual("fired,started,tools,resolved,done,tools", string.Join(",", seen));
        }

        [Test]
        public void ConversationCleared_FiresWhenAChatIsOpenedOrCleared_NotWhenATurnBegins()
        {
            ConsoleReducer r = NewReducer();
            int n = 0;
            r.ConversationCleared += () => n++;

            r.BeginTurn("hello");
            Assert.AreEqual(0, n, "a new turn continues the same conversation");

            r.LoadTranscript(new List<StoredMessage>());
            Assert.AreEqual(1, n);
            r.ClearConversation();
            Assert.AreEqual(2, n);
            r.Reset();
            Assert.AreEqual(3, n);
        }

        // ---- the tracker

        [Test]
        public void AStartedAndEndedRead_GrowsTheTree_AndNamesTheOwningServer()
        {
            ConsoleReducer r = NewReducer();
            MemoryTracker m = new MemoryTracker(r);
            int serverEvents = 0;
            m.ServerChanged += () => serverEvents++;
            Assert.IsNull(m.Server);

            r.Apply(AgentEvent.ToolStart(ReadTool, "{\"path\": \"Projects/Eva AI.md\"}", "a"));
            Assert.AreEqual(0, m.Tree.Count, "nothing grows until the call succeeds");

            r.Apply(AgentEvent.ToolEnd(ReadTool, ReadOut("Projects/Eva AI.md"), "a"));
            Assert.AreEqual(3, m.Tree.Count);
            Assert.IsTrue(m.Tree.TryFindNote("Projects/Eva AI.md", out MemoryNode n));
            Assert.AreEqual(MemoryAccessKind.Read, n.State);
            Assert.AreEqual("memory", m.Server);
            Assert.AreEqual(1, serverEvents);

            r.Apply(AgentEvent.ToolStart(ReadTool, "{\"path\": \"Projects/Eva AI.md\"}", "b"));
            r.Apply(AgentEvent.ToolEnd(ReadTool, ReadOut("Projects/Eva AI.md"), "b"));
            Assert.AreEqual(3, m.Tree.Count);
            Assert.AreEqual(2, n.AccessCount);
            Assert.AreEqual(1, serverEvents, "the server did not change");
        }

        [Test]
        public void AToolError_GrowsNothing()
        {
            ConsoleReducer r = NewReducer();
            MemoryTracker m = new MemoryTracker(r);
            r.Apply(AgentEvent.ToolStart(ReadTool, "{\"path\": \"Projects/Eva AI.md\"}", "a"));
            r.Apply(AgentEvent.ToolError(ReadTool, "vault offline", "a"));
            Assert.AreEqual(0, m.Tree.Count);
            Assert.IsNull(m.Server);
        }

        [Test]
        public void ASearch_AddsEveryHit_AndAReadUpgradesOne()
        {
            ConsoleReducer r = NewReducer();
            MemoryTracker m = new MemoryTracker(r);
            string search = Q("content=[{'type': 'text', 'text': '{`query`: `x`, `count`: 2, `results`: [{`path`: `A/one.md`}, {`path`: `B/two.md`}]}'}]");
            r.Apply(AgentEvent.ToolStart("fates_desktop__vault_search", "{\"query\": \"x\"}", "s"));
            r.Apply(AgentEvent.ToolEnd("fates_desktop__vault_search", search, "s"));
            Assert.AreEqual(5, m.Tree.Count);
            Assert.AreEqual("fates_desktop", m.Server, "the server comes from the tool name's prefix");

            m.Tree.TryFindNote("B/two.md", out MemoryNode two);
            Assert.AreEqual(MemoryAccessKind.Hit, two.State);
            r.Apply(AgentEvent.ToolStart("fates_desktop__vault_read", "{\"path\": \"B/two.md\"}", "r"));
            r.Apply(AgentEvent.ToolEnd("fates_desktop__vault_read", "…truncated", "r"));
            Assert.AreEqual(MemoryAccessKind.Read, two.State, "the input path is enough even when the output is useless");
            Assert.AreEqual(5, m.Tree.Count);
        }

        [Test]
        public void ANonMemoryTool_GrowsNothing()
        {
            ConsoleReducer r = NewReducer();
            MemoryTracker m = new MemoryTracker(r);
            r.Apply(AgentEvent.ToolStart("fate_ubuntu_server__read_file", "{\"path\": \"/etc/hosts\"}", "a"));
            r.Apply(AgentEvent.ToolEnd("fate_ubuntu_server__read_file", ReadOut("etc/hosts"), "a"));
            Assert.AreEqual(0, m.Tree.Count);
        }

        [Test]
        public void LiveChanged_RunsWhileAReadIsOut_AndNeverForASearch()
        {
            ConsoleReducer r = NewReducer();
            MemoryTracker m = new MemoryTracker(r);
            List<string> log = new List<string>();
            m.LiveChanged += (p, on) => log.Add((on ? "+" : "-") + p);

            r.Apply(AgentEvent.ToolStart("memory__vault_search", "{\"query\": \"x\"}", "s"));
            r.Apply(AgentEvent.ToolEnd("memory__vault_search", "none", "s"));
            Assert.AreEqual(0, log.Count);

            r.Apply(AgentEvent.ToolStart(ReadTool, "{\"path\": \"A/b.md\"}", "a"));
            r.Apply(AgentEvent.ToolStart("memory__vault_append", "{\"path\": \"C/d.md\", \"content\": \"x\"}", "b"));
            r.Apply(AgentEvent.ToolError(ReadTool, "boom", "a"));
            r.Apply(AgentEvent.ToolEnd("memory__vault_append", "{\"path\": \"C/d.md\"}", "b"));
            Assert.AreEqual("+A/b.md,+C/d.md,-A/b.md,-C/d.md", string.Join(",", log));
            Assert.AreEqual(3, m.Tree.Count, "only the successful append grew the tree: root, folder, note");
            Assert.IsFalse(m.Tree.TryFindNote("A/b.md", out _));
            Assert.IsTrue(m.Tree.TryFindNote("C/d.md", out _));
        }

        [Test]
        public void ANoteCreatedByTheCallItself_IsInTheTreeWhenItsLiveFlagDrops()
        {
            ConsoleReducer r = NewReducer();
            MemoryTracker m = new MemoryTracker(r);
            bool existedOnDrop = false;
            m.LiveChanged += (p, on) =>
            {
                if (!on) existedOnDrop = m.Tree.TryFindNote(p, out _);
            };
            r.Apply(AgentEvent.ToolStart("memory__vault_write_note", "{\"path\": \"New/n.md\"}", "w"));
            r.Apply(AgentEvent.ToolEnd("memory__vault_write_note", "ok", "w"));
            Assert.IsTrue(existedOnDrop);
        }

        [Test]
        public void ChangingTheConversation_ClearsTheTree_ButANewTurnDoesNot()
        {
            ConsoleReducer r = NewReducer();
            MemoryTracker m = new MemoryTracker(r);
            r.Apply(AgentEvent.ToolStart(ReadTool, "{\"path\": \"A/b.md\"}", "a"));
            r.Apply(AgentEvent.ToolEnd(ReadTool, "x", "a"));
            Assert.AreEqual(3, m.Tree.Count);

            r.BeginTurn("and another thing");
            Assert.AreEqual(3, m.Tree.Count, "the same session continues");

            r.LoadTranscript(new List<StoredMessage>());
            Assert.AreEqual(0, m.Tree.Count, "another chat is another session");

            r.Apply(AgentEvent.ToolStart(ReadTool, "{\"path\": \"A/b.md\"}", "b"));
            r.Apply(AgentEvent.ToolEnd(ReadTool, "x", "b"));
            Assert.AreEqual(3, m.Tree.Count);
            r.ClearConversation();
            Assert.AreEqual(0, m.Tree.Count, "New chat");
        }

        [Test]
        public void RebuildingTheGraph_ClearsTheTree()
        {
            ConsoleReducer r = NewReducer();
            MemoryTracker m = new MemoryTracker(r);
            r.Apply(AgentEvent.ToolStart(ReadTool, "{\"path\": \"A/b.md\"}", "a"));
            r.Apply(AgentEvent.ToolEnd(ReadTool, "x", "a"));
            r.Graph.Build(SampleInventory.Eva());
            Assert.AreEqual(0, m.Tree.Count);
        }

        [Test]
        public void ADisposedTracker_StopsListening()
        {
            ConsoleReducer r = NewReducer();
            MemoryTracker m = new MemoryTracker(r);
            m.Dispose();
            r.Apply(AgentEvent.ToolStart(ReadTool, "{\"path\": \"A/b.md\"}", "a"));
            r.Apply(AgentEvent.ToolEnd(ReadTool, "x", "a"));
            Assert.AreEqual(0, m.Tree.Count);
        }

        // ---- the replay's memory sequence

        [Test]
        public void TheReplayRunWithItsMemorySequence_GrowsASensibleTree()
        {
            ConsoleReducer r = NewReducer();
            MemoryTracker m = new MemoryTracker(r);
            IReadOnlyList<ScriptStep> steps = ReplayTransport.Merge(ReplayTransport.Script, ReplayTransport.MemoryScript);
            foreach (ScriptStep s in steps)
            {
                _now = s.AtMs;
                r.Apply(s.Event);
            }

            Assert.AreEqual("memory", m.Server);
            // The script's own read (Oct 29) and append (Nov 2), plus the search's six, three reads and one new note.
            Assert.AreEqual(8, m.Tree.NoteCount);
            Assert.AreEqual(14, m.Tree.Count);
            AssertNote(m, "Trading/2026-10-29.md", MemoryAccessKind.Read);
            AssertNote(m, "Trading/Playbook.md", MemoryAccessKind.Read);
            AssertNote(m, "Projects/Trading Bot.md", MemoryAccessKind.Read);
            AssertNote(m, "Engineering/Roadmap.md", MemoryAccessKind.Read);
            AssertNote(m, "Projects/Eva AI.md", MemoryAccessKind.Hit);
            AssertNote(m, "Eva/eva.md", MemoryAccessKind.Hit);
            AssertNote(m, "Trading/2026-11-02.md", MemoryAccessKind.Written);
            AssertNote(m, "Trading/Reviews/2026-11-02 Positions Review.md", MemoryAccessKind.Written);
            Assert.AreEqual(0, r.State.RunningToolCount);
        }

        private static void AssertNote(MemoryTracker m, string path, MemoryAccessKind expected)
        {
            Assert.IsTrue(m.Tree.TryFindNote(path, out MemoryNode n), path);
            Assert.AreEqual(expected, n.State, path);
        }

        [Test]
        public void TheMemorySequence_IsInsertedInTimeOrder_WithoutMovingAnyScriptEvent()
        {
            IReadOnlyList<ScriptStep> merged = ReplayTransport.Merge(ReplayTransport.Script, ReplayTransport.MemoryScript);
            Assert.AreEqual(ReplayTransport.Script.Count + ReplayTransport.MemoryScript.Count, merged.Count);
            for (int i = 1; i < merged.Count; i++) Assert.GreaterOrEqual(merged[i].AtMs, merged[i - 1].AtMs);

            // The prototype's events keep their relative order and their times.
            int next = 0;
            foreach (ScriptStep s in merged)
            {
                if (next < ReplayTransport.Script.Count && s.AtMs == ReplayTransport.Script[next].AtMs
                    && s.Event.ToString() == ReplayTransport.Script[next].Event.ToString()) next++;
            }
            Assert.AreEqual(ReplayTransport.Script.Count, next);

            // Every memory event is a real shape: a vault tool on the replay's own memory server, with its own run id.
            foreach (ScriptStep s in ReplayTransport.MemoryScript)
            {
                StringAssert.StartsWith("memory__vault_", s.Event.Tool);
                StringAssert.StartsWith("m", s.Event.RunId);
            }
        }

        [Test]
        public void MergeKeepsTheFirstListFirstOnATie()
        {
            ScriptStep[] a = { new ScriptStep(100, AgentEvent.Thought("a")) };
            ScriptStep[] b = { new ScriptStep(100, AgentEvent.Content("b")) };
            IReadOnlyList<ScriptStep> m = ReplayTransport.Merge(a, b);
            Assert.AreEqual(AgentEventKind.Thought, m[0].Event.Kind);
            Assert.AreEqual(AgentEventKind.Content, m[1].Event.Kind);
        }

        [Test]
        public void TheDefaultReplay_IsStillThePrototypesScript()
        {
            using (ReplayTransport t = new ReplayTransport())
            {
                Assert.IsNotNull(t);
            }
            Assert.AreEqual(20, ReplayTransport.Script.Count, "the memory sequence is not in the pinned script");
        }
    }
}
