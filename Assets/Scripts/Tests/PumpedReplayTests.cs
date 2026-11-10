using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using AgentConsole.Domain;
using AgentConsole.Transport;

namespace AgentConsole.Tests
{
    /// <summary>
    /// The thread-free replay (Unity WebGL has no thread pool): the same script, driven by Pump with a fake clock.
    /// Nothing here sleeps or waits, which is itself the point: the run is a pure function of the clock.
    /// </summary>
    public class PumpedReplayTests
    {
        private static List<string> Drain(ReplayTransport t)
        {
            List<string> got = new List<string>();
            while (t.TryDequeue(out AgentEvent ev)) got.Add(ev.ToString());
            return got;
        }

        private static List<string> Expected(IReadOnlyList<ScriptStep> steps, bool withEnd)
        {
            List<string> e = new List<string>();
            foreach (ScriptStep s in steps) e.Add(s.Event.ToString());
            if (withEnd) e.Add(AgentEvent.RunEnded().ToString());
            return e;
        }

        [Test]
        public void SendAsync_CompletesAtOnce_AndEmitsNothingUntilPumped()
        {
            using (ReplayTransport t = new ReplayTransport(pumped: true))
            {
                var task = t.SendAsync(null, "hi", CancellationToken.None);
                Assert.IsTrue(task.IsCompleted);
                Assert.AreEqual(ReplayTransport.ReplayJobId, task.Result);
                Assert.AreEqual(0, Drain(t).Count);
            }
        }

        [Test]
        public void Pump_PlaysTheScriptInOrder_AtTheScriptedTimes_ThenEnds()
        {
            using (ReplayTransport t = new ReplayTransport(pumped: true))
            {
                t.SendAsync(null, "hi", CancellationToken.None);
                const double start = 100.0; // an arbitrary clock origin: only differences matter
                List<string> got = new List<string>();

                t.Pump(start);
                got.AddRange(Drain(t));
                Assert.AreEqual(1, got.Count); // only the 0 ms Meta is due

                t.Pump(start + 0.149);
                Assert.AreEqual(0, Drain(t).Count); // ChatModelStart is at 150 ms
                t.Pump(start + 0.151);
                got.AddRange(Drain(t));
                Assert.AreEqual(2, got.Count);

                t.Pump(start + 12.999);
                got.AddRange(Drain(t));
                Assert.AreEqual(ReplayTransport.Script.Count - 1, got.Count); // the 13000 ms LlmEnd is not due yet

                t.Pump(start + 13.001);
                got.AddRange(Drain(t));
                CollectionAssert.AreEqual(Expected(ReplayTransport.Script, true), got);

                t.Pump(start + 60.0); // a finished run emits nothing more
                Assert.AreEqual(0, Drain(t).Count);
            }
        }

        [Test]
        public void Pump_CatchesUpOnALongFrame_WithoutReorderingOrDropping()
        {
            using (ReplayTransport t = new ReplayTransport(pumped: true))
            {
                t.SendAsync(null, "hi", CancellationToken.None);
                t.Pump(0.0);
                t.Pump(1000.0); // a tab that was in the background for a long time
                CollectionAssert.AreEqual(Expected(ReplayTransport.Script, true), Drain(t));
            }
        }

        [Test]
        public void Pump_WithTheMemoryTree_PlaysTheMergedScript_InTheThreadedOrder()
        {
            using (ReplayTransport t = new ReplayTransport(memoryTree: true, pumped: true))
            {
                t.SendAsync(null, "hi", CancellationToken.None);
                t.Pump(0.0);
                t.Pump(20.0);
                IReadOnlyList<ScriptStep> merged = ReplayTransport.Merge(ReplayTransport.Script, ReplayTransport.MemoryScript);
                Assert.AreEqual(ReplayTransport.Script.Count + ReplayTransport.MemoryScript.Count, merged.Count);
                CollectionAssert.AreEqual(Expected(merged, true), Drain(t));
            }
        }

        [Test]
        public void Pump_HonoursTheSpeedMultiplier()
        {
            using (ReplayTransport t = new ReplayTransport(speed: 10, pumped: true))
            {
                t.SendAsync(null, "hi", CancellationToken.None);
                t.Pump(0.0);
                Drain(t);
                t.Pump(0.0149); // 14.9 ms of wall time is 149 ms of script (the step is at 150)
                Assert.AreEqual(0, Drain(t).Count);
                t.Pump(0.0151);
                Assert.AreEqual(1, Drain(t).Count);
                t.Pump(1.3); // 13 s of script
                Assert.AreEqual(ReplayTransport.Script.Count - 2 + 1, Drain(t).Count);
            }
        }

        [Test]
        public void Stop_EmitsStopped_OnceAndTheRunProducesNothingAfterIt()
        {
            using (ReplayTransport t = new ReplayTransport(pumped: true))
            {
                t.SendAsync(null, "hi", CancellationToken.None);
                t.Pump(0.0);
                t.Pump(2.0);
                Drain(t);

                t.StopAsync(ReplayTransport.ReplayJobId).GetAwaiter().GetResult();
                t.StopAsync(ReplayTransport.ReplayJobId).GetAwaiter().GetResult(); // a second stop is a no-op
                CollectionAssert.AreEqual(new[] { AgentEvent.Error("Stopped.").ToString() }, Drain(t));

                t.Pump(30.0);
                Assert.AreEqual(0, Drain(t).Count);
            }
        }

        [Test]
        public void Stop_AfterTheRunFinished_EmitsNothing()
        {
            using (ReplayTransport t = new ReplayTransport(pumped: true))
            {
                t.SendAsync(null, "hi", CancellationToken.None);
                t.Pump(0.0);
                t.Pump(14.0);
                Drain(t);
                t.StopAsync(ReplayTransport.ReplayJobId).GetAwaiter().GetResult();
                Assert.AreEqual(0, Drain(t).Count);
            }
        }

        [Test]
        public void ANewSend_ReplacesTheRunBeforeIt_WithoutAStoppedEvent_AndRestartsTheClock()
        {
            using (ReplayTransport t = new ReplayTransport(pumped: true))
            {
                t.SendAsync(null, "one", CancellationToken.None);
                t.Pump(0.0);
                t.Pump(2.0);
                Drain(t);

                t.SendAsync(null, "two", CancellationToken.None);
                t.Pump(50.0); // the second run's time zero
                List<string> got = Drain(t);
                Assert.AreEqual(1, got.Count);
                Assert.AreEqual(ReplayTransport.Script[0].Event.ToString(), got[0]);
                t.Pump(63.0);
                Assert.AreEqual(Expected(ReplayTransport.Script, true).Count - 1, Drain(t).Count);
            }
        }

        [Test]
        public void ACancelledToken_EndsThePumpedRunQuietly()
        {
            using (ReplayTransport t = new ReplayTransport(pumped: true))
            using (CancellationTokenSource cts = new CancellationTokenSource())
            {
                t.SendAsync(null, "hi", cts.Token);
                t.Pump(0.0);
                Drain(t);
                cts.Cancel();
                t.Pump(5.0);
                Assert.AreEqual(0, Drain(t).Count);
            }
        }

        [Test]
        public void Pump_IsInert_OnTheThreadedTransport()
        {
            using (ReplayTransport t = new ReplayTransport(speed: 1000))
            {
                Assert.AreEqual(0, t.Pump(0.0));
                Assert.AreEqual(0, t.Pump(100.0));
            }
        }

        // ------------------------------------------------------------------ the rest of the replay path

        [Test]
        public void ChatSession_Inline_RunsAWholeReplayTurn_OnTheCallersThread_WithNoThreadPool()
        {
            ConsoleReducer r = new ConsoleReducer(new GraphModel("EVA", SampleInventory.Eva()), () => 0);
            MemoryChatStore store = new MemoryChatStore();
            using (ReplayTransport t = new ReplayTransport(memoryTree: true, pumped: true))
            using (ChatSession s = new ChatSession(r, t, store, runInline: true))
            {
                s.Submit("Cross-check my notes.");

                // No waiting anywhere: the chat exists and the user's message is already saved, because nothing hopped threads.
                IReadOnlyList<ChatSummary> chats = store.ListAsync(CancellationToken.None).Result;
                Assert.AreEqual(1, chats.Count);
                string id = chats[0].Id;
                Assert.AreEqual(1, store.MessagesOf(id).Count);
                Assert.IsTrue(r.State.RunInFlight);

                s.Pump();
                Assert.AreEqual(id, s.SelectedId);

                double now = 0.0;
                for (int frame = 0; frame < 2000 && r.State.RunInFlight; frame++)
                {
                    now += 0.05;
                    t.Pump(now);
                    while (t.TryDequeue(out AgentEvent ev)) r.Apply(ev);
                    s.Pump();
                }

                Assert.IsFalse(r.State.RunInFlight);
                s.Pump();
                IReadOnlyList<StoredMessage> saved = store.MessagesOf(id);
                Assert.AreEqual(2, saved.Count); // the user's message, then the reply, in that order
                Assert.AreEqual("user", saved[0].Role);
                Assert.AreEqual("assistant", saved[1].Role);
                StringAssert.Contains("ENPH", saved[1].Content);
                Assert.IsNull(s.Error);
            }
        }

        [Test]
        public void ChatSession_Inline_QueuedPrompt_SendsAfterTheRunFinishes_AndStopClearsIt()
        {
            ConsoleReducer r = new ConsoleReducer(new GraphModel("EVA", SampleInventory.Eva()), () => 0);
            using (ReplayTransport t = new ReplayTransport(pumped: true))
            using (ChatSession s = new ChatSession(r, t, new MemoryChatStore(), runInline: true))
            {
                s.Submit("one");
                s.Submit("two"); // a run is in flight: queued
                Assert.AreEqual(1, s.QueuedCount);

                double now = 0.0;
                t.Pump(now);
                now += 14.0;
                t.Pump(now);
                while (t.TryDequeue(out AgentEvent ev)) r.Apply(ev);
                s.Pump();

                Assert.AreEqual(0, s.QueuedCount); // the queued one started when the first finished
                Assert.IsTrue(r.State.RunInFlight);

                s.Submit("three");
                Assert.AreEqual(1, s.QueuedCount);
                s.Stop(); // the replay runs and the queue is dropped
                // A few frames, as the host loop would run them: the stop can only be sent once the run's job id has
                // arrived, which takes one pump-and-drain, and its "Stopped." then needs another to reach the reducer.
                for (int frame = 1; frame <= 4; frame++)
                {
                    t.Pump(now + 0.1 * frame);
                    while (t.TryDequeue(out AgentEvent ev)) r.Apply(ev);
                    s.Pump();
                }
                Assert.AreEqual(0, s.QueuedCount);
                Assert.IsFalse(r.State.RunInFlight);
            }
        }

        [Test]
        public void ModelController_WithNoSelector_IsInert()
        {
            // The replay advertises no model capability, so the bootstrap hands the controller a null selector.
            using (ReplayTransport t = new ReplayTransport(pumped: true))
            using (ModelController m = new ModelController(t.Supports(AgentCapability.Models) ? (IAgentTransport)t as IModelSelector : null))
            {
                Assert.IsFalse(t.Supports(AgentCapability.Models));
                Assert.IsFalse(m.IsAvailable);
                Assert.AreEqual(ModelControlPhase.Unavailable, m.Phase);
                m.Refresh();
                m.Select("anything");
                Assert.IsFalse(m.Pump());
                Assert.AreEqual(ModelControlPhase.Unavailable, m.Phase);
            }
        }
    }
}
