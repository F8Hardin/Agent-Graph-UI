using System.Collections.Generic;
using NUnit.Framework;
using AgentConsole.Domain;
using AgentConsole.Transport;

namespace AgentConsole.Tests
{
    public class ReducerTests
    {
        private long _now;

        private ConsoleReducer NewReducer()
        {
            _now = 0;
            return new ConsoleReducer(new GraphModel("EVA", SampleInventory.Eva()), () => _now);
        }

        private const string Positions = "robinhood__get_equity_positions";

        [Test]
        public void TwoConcurrentStartsOfTheSameTool_ResolveToTheRightCardsByRunId()
        {
            ConsoleReducer r = NewReducer();
            r.Apply(AgentEvent.ToolStart(Positions, "{\"acct\":1}", "A"));
            _now = 100;
            r.Apply(AgentEvent.ToolStart(Positions, "{\"acct\":2}", "B"));
            Assert.AreEqual(2, r.State.ToolCalls.Count);
            Assert.AreEqual(2, r.State.RunningToolCount);

            // B finishes first, though A started first: arrival order must not decide the match.
            _now = 250;
            r.Apply(AgentEvent.ToolEnd(Positions, "out-B", "B"));

            ToolCall a = r.State.ToolCalls[0];
            ToolCall b = r.State.ToolCalls[1];
            Assert.AreEqual("A", a.RunId);
            Assert.AreEqual("{\"acct\":1}", a.Input);
            Assert.AreEqual(ToolStatus.Running, a.Status);
            Assert.IsNull(a.Output);
            Assert.AreEqual("B", b.RunId);
            Assert.AreEqual(ToolStatus.Done, b.Status);
            Assert.AreEqual("out-B", b.Output);
            Assert.AreEqual(150, b.DurationMs);

            _now = 400;
            r.Apply(AgentEvent.ToolEnd(Positions, "out-A", "A"));
            Assert.AreEqual(ToolStatus.Done, a.Status);
            Assert.AreEqual("out-A", a.Output);
            Assert.AreEqual(400, a.DurationMs);
            Assert.AreEqual("out-B", b.Output, "B must not be overwritten by A's end");
            Assert.AreEqual(0, r.State.RunningToolCount);
        }

        [Test]
        public void ToolError_MarksOnlyItsOwnCard()
        {
            ConsoleReducer r = NewReducer();
            r.Apply(AgentEvent.ToolStart(Positions, "{}", "A"));
            r.Apply(AgentEvent.ToolStart(Positions, "{}", "B"));
            r.Apply(AgentEvent.ToolStart("memory__vault_read", "{}", "C"));

            r.Apply(AgentEvent.ToolError(Positions, "MCP wedged", "A"));

            Assert.AreEqual(ToolStatus.Error, r.State.ToolCalls[0].Status);
            Assert.AreEqual("MCP wedged", r.State.ToolCalls[0].Output);
            Assert.AreEqual(ToolStatus.Running, r.State.ToolCalls[1].Status);
            Assert.AreEqual(ToolStatus.Running, r.State.ToolCalls[2].Status);
            Assert.AreEqual(2, r.State.RunningToolCount);
        }

        [Test]
        public void ConsecutiveContentDeltas_AppendToOneMessage()
        {
            ConsoleReducer r = NewReducer();
            string expected = "";
            for (int i = 0; i < 50; i++)
            {
                string delta = "tok" + i + " ";
                expected += delta;
                r.Apply(AgentEvent.Content(delta));
            }

            Assert.AreEqual(1, r.State.Transcript.Count);
            ChatMessage m = r.State.Transcript[0];
            Assert.AreEqual(MessageRole.Assistant, m.Role);
            Assert.IsTrue(m.IsOpen);
            Assert.AreEqual(expected, m.Text);
            Assert.AreEqual(expected.Length, m.Length);
            Assert.AreEqual(50, m.Version);
        }

        [Test]
        public void ThoughtThenContent_MakesTwoMessagesAndClosesTheFirst()
        {
            ConsoleReducer r = NewReducer();
            r.Apply(AgentEvent.Thought("thinking "));
            r.Apply(AgentEvent.Thought("more"));
            r.Apply(AgentEvent.Content("answer"));

            Assert.AreEqual(2, r.State.Transcript.Count);
            Assert.AreEqual(MessageRole.Reasoning, r.State.Transcript[0].Role);
            Assert.AreEqual("thinking more", r.State.Transcript[0].Text);
            Assert.IsFalse(r.State.Transcript[0].IsOpen);
            Assert.AreEqual(MessageRole.Assistant, r.State.Transcript[1].Role);
            Assert.IsTrue(r.State.Transcript[1].IsOpen);

            r.Apply(AgentEvent.LlmEnd());
            Assert.IsFalse(r.State.Transcript[1].IsOpen);
        }

        [Test]
        public void ToolCallsBetweenThoughts_DoNotSplitTheReasoningMessage()
        {
            ConsoleReducer r = NewReducer();
            r.Apply(AgentEvent.Thought("a"));
            r.Apply(AgentEvent.ToolStart("memory__vault_search", "{}", "r1"));
            r.Apply(AgentEvent.ToolEnd("memory__vault_search", "ok", "r1"));
            r.Apply(AgentEvent.Thought("b"));

            Assert.AreEqual(1, r.State.Transcript.Count);
            Assert.AreEqual("ab", r.State.Transcript[0].Text);
        }

        [Test]
        public void Phase_FollowsTheRun()
        {
            ConsoleReducer r = NewReducer();
            List<Phase> seen = new List<Phase>();
            r.PhaseChanged += p => seen.Add(p);

            r.Apply(AgentEvent.ChatModelStart());
            r.Apply(AgentEvent.ToolStart("memory__vault_search", "{}", "m1"));
            r.Apply(AgentEvent.ToolEnd("memory__vault_search", "ok", "m1"));
            r.Apply(AgentEvent.ToolStart(Positions, "{}", "t1"));
            r.Apply(AgentEvent.ToolEnd(Positions, "ok", "t1"));
            r.Apply(AgentEvent.Content("hi"));
            r.Apply(AgentEvent.LlmEnd());
            r.Apply(AgentEvent.RunEnded());

            CollectionAssert.AreEqual(
                new[] { Phase.Thinking, Phase.Memory, Phase.Thinking, Phase.ToolCall, Phase.Thinking, Phase.Talking, Phase.Thinking, Phase.Idle },
                seen);
        }

        [Test]
        public void Phase_StaysInToolCallWhileAnotherCallIsStillRunning()
        {
            ConsoleReducer r = NewReducer();
            r.Apply(AgentEvent.ToolStart(Positions, "{}", "A"));
            r.Apply(AgentEvent.ToolStart("memory__vault_read", "{}", "B"));
            r.Apply(AgentEvent.ToolEnd("memory__vault_read", "ok", "B"));
            Assert.AreEqual(Phase.ToolCall, r.State.Phase);
            r.Apply(AgentEvent.ToolEnd(Positions, "ok", "A"));
            Assert.AreEqual(Phase.Thinking, r.State.Phase);
        }

        [Test]
        public void NodeFiredAndResolved_CarryTheToolNodeId()
        {
            ConsoleReducer r = NewReducer();
            List<string> fired = new List<string>();
            List<string> resolved = new List<string>();
            r.NodeFired += id => fired.Add(id);
            r.NodeResolved += id => resolved.Add(id);

            r.Apply(AgentEvent.ToolStart("memory__vault_search", "{}", "r1"));
            r.Apply(AgentEvent.ToolEnd("memory__vault_search", "ok", "r1"));

            CollectionAssert.AreEqual(new[] { "tool:memory__vault_search" }, fired);
            CollectionAssert.AreEqual(new[] { "tool:memory__vault_search" }, resolved);
            Assert.IsTrue(r.State.ToolCalls[0].IsMemory);
        }

        [Test]
        public void ChangeEvents_FireOnTheirOwnState()
        {
            ConsoleReducer r = NewReducer();
            int transcript = 0, tools = 0;
            r.TranscriptChanged += () => transcript++;
            r.ToolsChanged += () => tools++;

            r.Apply(AgentEvent.Content("x"));
            Assert.AreEqual(1, transcript);
            Assert.AreEqual(0, tools);

            r.Apply(AgentEvent.ToolStart(Positions, "{}", "A"));
            r.Apply(AgentEvent.ToolEnd(Positions, "ok", "A"));
            Assert.AreEqual(2, tools);
        }

        [Test]
        public void UnseenTool_IsGraftedOnFirstUse_AndFiresItsNewNode()
        {
            ConsoleReducer r = NewReducer();
            List<string> fired = new List<string>();
            r.NodeFired += id => fired.Add(id);
            int before = r.Graph.Nodes.Count;

            r.Apply(AgentEvent.ToolStart("robinhood__get_pnl_trade_history", "{}", "A"));

            Assert.AreEqual(before + 1, r.Graph.Nodes.Count);
            Assert.IsTrue(r.Graph.TryGetNode("tool:robinhood__get_pnl_trade_history", out GraphNode node));
            Assert.IsTrue(node.Grafted);
            CollectionAssert.AreEqual(new[] { "tool:robinhood__get_pnl_trade_history" }, fired);
            Assert.AreEqual("robinhood", r.State.ToolCalls[0].Server);
            Assert.AreEqual("get_pnl_trade_history", r.State.ToolCalls[0].Tool);
        }

        [Test]
        public void DuplicateToolStart_WithTheSameRunId_DoesNotAddASecondCard()
        {
            ConsoleReducer r = NewReducer();
            r.Apply(AgentEvent.ToolStart(Positions, "{}", "A"));
            r.Apply(AgentEvent.ToolStart(Positions, "{}", "A"));
            Assert.AreEqual(1, r.State.ToolCalls.Count);
            Assert.AreEqual(1, r.State.RunningToolCount);
        }

        [Test]
        public void ToolEnd_ForAnUnseenRunId_IsKeptRatherThanDropped()
        {
            ConsoleReducer r = NewReducer();
            r.Apply(AgentEvent.ToolEnd("memory__vault_read", "late", "ghost"));
            Assert.AreEqual(1, r.State.ToolCalls.Count);
            Assert.AreEqual(ToolStatus.Done, r.State.ToolCalls[0].Status);
            Assert.AreEqual("late", r.State.ToolCalls[0].Output);
            Assert.AreEqual(0, r.State.RunningToolCount);
        }

        [Test]
        public void ToolEnd_WithNoRunId_FallsBackToTheOldestRunningCallOfThatTool()
        {
            ConsoleReducer r = NewReducer();
            r.Apply(AgentEvent.ToolStart(Positions, "{}", "A"));
            r.Apply(AgentEvent.ToolStart(Positions, "{}", "B"));
            r.Apply(AgentEvent.ToolEnd(Positions, "first", null));
            Assert.AreEqual(ToolStatus.Done, r.State.ToolCalls[0].Status);
            Assert.AreEqual(ToolStatus.Running, r.State.ToolCalls[1].Status);
        }

        [Test]
        public void LlmEnd_BetweenModelTurns_DoesNotFlashIdle()
        {
            ConsoleReducer r = NewReducer();
            List<Phase> seen = new List<Phase>();
            r.PhaseChanged += p => seen.Add(p);

            // EVA emits llm_end after every model turn: turn 1 calls a tool, turn 2 answers.
            r.Apply(AgentEvent.ChatModelStart());
            r.Apply(AgentEvent.ToolStart(Positions, "{}", "A"));
            r.Apply(AgentEvent.ToolEnd(Positions, "ok", "A"));
            r.Apply(AgentEvent.LlmEnd());
            Assert.AreEqual(Phase.Thinking, r.State.Phase, "llm_end between turns must not go Idle");
            r.Apply(AgentEvent.ChatModelStart());
            r.Apply(AgentEvent.Content("done"));
            r.Apply(AgentEvent.LlmEnd());
            Assert.AreEqual(Phase.Thinking, r.State.Phase);
            CollectionAssert.DoesNotContain(seen, Phase.Idle);

            r.Apply(AgentEvent.RunEnded());
            Assert.AreEqual(Phase.Idle, r.State.Phase);
        }

        [Test]
        public void LlmEnd_KeepsThePhaseOfACallStillInFlight()
        {
            ConsoleReducer r = NewReducer();
            r.Apply(AgentEvent.ToolStart("memory__vault_read", "{}", "M"));
            r.Apply(AgentEvent.LlmEnd());
            Assert.AreEqual(Phase.Memory, r.State.Phase);
            r.Apply(AgentEvent.ToolEnd("memory__vault_read", "ok", "M"));
            Assert.AreEqual(Phase.Thinking, r.State.Phase);
        }

        [Test]
        public void RunEnded_GoesIdleAndClosesTheOpenMessage()
        {
            ConsoleReducer r = NewReducer();
            int transcript = 0;
            r.Apply(AgentEvent.Content("streaming"));
            r.TranscriptChanged += () => transcript++;
            Assert.IsTrue(r.State.Transcript[0].IsOpen);

            r.Apply(AgentEvent.RunEnded());

            Assert.AreEqual(Phase.Idle, r.State.Phase);
            Assert.IsFalse(r.State.Transcript[0].IsOpen);
            Assert.AreEqual(1, transcript);
        }

        [Test]
        public void ErrorEvent_AddsAnErrorMessageAndGoesIdle()
        {
            ConsoleReducer r = NewReducer();
            r.Apply(AgentEvent.Content("partial"));
            r.Apply(AgentEvent.Error("boom"));
            Assert.AreEqual(2, r.State.Transcript.Count);
            Assert.IsFalse(r.State.Transcript[0].IsOpen);
            Assert.IsTrue(r.State.Transcript[1].IsError);
            Assert.AreEqual("boom", r.State.Transcript[1].Text);
            Assert.AreEqual(Phase.Idle, r.State.Phase);
        }

        [Test]
        public void MetaAndUserMessage_AndReset()
        {
            ConsoleReducer r = NewReducer();
            r.AddUserMessage("hello");
            r.Apply(AgentEvent.Meta("m", 0.4f, "job1"));
            r.Apply(AgentEvent.ToolStart(Positions, "{}", "A"));
            Assert.AreEqual("m", r.State.Model);
            Assert.AreEqual("job1", r.State.JobId);
            Assert.AreEqual(MessageRole.User, r.State.Transcript[0].Role);
            Assert.IsFalse(r.State.Transcript[0].IsOpen);

            r.Reset();
            Assert.AreEqual(0, r.State.Transcript.Count);
            Assert.AreEqual(0, r.State.ToolCalls.Count);
            Assert.AreEqual(0, r.State.RunningToolCount);
            Assert.IsNull(r.State.Model);
            Assert.AreEqual(Phase.Idle, r.State.Phase);
        }

        [Test]
        public void PrototypeScript_ReducesToTheExpectedState()
        {
            ConsoleReducer r = NewReducer();
            foreach (ScriptStep step in ReplayTransport.Script)
            {
                _now = step.AtMs;
                r.Apply(step.Event);
            }

            Assert.AreEqual(6, r.State.ToolCalls.Count);
            Assert.AreEqual(0, r.State.RunningToolCount);
            ToolStatus[] expected =
            {
                ToolStatus.Done, ToolStatus.Done, ToolStatus.Error, ToolStatus.Done, ToolStatus.Done, ToolStatus.Done
            };
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.AreEqual(expected[i], r.State.ToolCalls[i].Status, "call " + (i + 1));
            }

            // The first robinhood call failed and the retry succeeded: two cards, same tool, different RunIds.
            ToolCall failed = r.State.ToolCalls[2];
            ToolCall retry = r.State.ToolCalls[4];
            Assert.AreEqual("r3", failed.RunId);
            Assert.AreEqual("r5", retry.RunId);
            Assert.AreEqual(failed.Tool, retry.Tool);
            Assert.AreEqual("MCP wedged — Recv-Q 1459", failed.Output);
            Assert.AreEqual("4 positions", retry.Output);
            Assert.AreEqual(1200, r.State.ToolCalls[0].DurationMs);
            Assert.AreEqual(1200, r.State.ToolCalls[3].DurationMs);
            Assert.AreEqual("fate_ubuntu_server", r.State.ToolCalls[3].Server);

            Assert.AreEqual(2, r.State.Transcript.Count);
            Assert.AreEqual(MessageRole.Reasoning, r.State.Transcript[0].Role);
            Assert.AreEqual(MessageRole.Assistant, r.State.Transcript[1].Role);
            Assert.IsFalse(r.State.Transcript[1].IsOpen);
            StringAssert.StartsWith("Your Oct 29 note planned an ENPH exit", r.State.Transcript[1].Text);
            StringAssert.EndsWith("ENPH is still open.", r.State.Transcript[1].Text);

            // The script ends on llm_end, which settles to Thinking; only the transport's RunEnded goes Idle.
            Assert.AreEqual(Phase.Thinking, r.State.Phase);
            r.Apply(AgentEvent.RunEnded());
            Assert.AreEqual(Phase.Idle, r.State.Phase);
            Assert.AreEqual("qwen3.5-9b", r.State.Model);
            Assert.AreEqual(0.4f, r.State.Temperature.Value, 1e-6f);
            Assert.AreEqual(ReplayTransport.ReplayJobId, r.State.JobId);

            // Every tool in the script is in the inventory, so nothing was grafted.
            Assert.AreEqual(new GraphModel("EVA", SampleInventory.Eva()).Nodes.Count, r.Graph.Nodes.Count);
        }
    }
}
