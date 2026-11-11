using System.Threading;
using System.Threading.Tasks;

namespace AgentConsole.Domain
{
    /// <summary>
    /// Where the controllers' continuations run. Desktop hops through the thread pool; a host with no thread pool (the
    /// Unity WebGL player) must keep everything on the thread that called, because a continuation sent to the pool
    /// never runs there. One switch for <see cref="ChatSession"/>, <see cref="ModelController"/> and
    /// <see cref="InventoryController"/> so the choice is made once, in the composition root.
    /// </summary>
    public enum AsyncMode
    {
        /// <summary>The default and the desktop behaviour: requests run on the pool and continue wherever they finish.</summary>
        ThreadPool,

        /// <summary>
        /// No pool hops. A turn starts on the caller's thread and every await resumes on the captured
        /// SynchronizationContext (Unity's, on the main thread). The tasks may complete later, which is what real I/O does;
        /// the older "inline" mode assumed they were already complete.
        /// </summary>
        CallerContext
    }

    public static class AsyncModes
    {
        /// <summary>
        /// The scheduler for ContinueWith. Must be called on the thread whose context should be resumed (the controller
        /// constructor, on the main thread). With no SynchronizationContext at all it falls back to the default scheduler,
        /// where ExecuteSynchronously still runs a continuation on whichever thread completed the task.
        /// </summary>
        public static TaskScheduler SchedulerFor(AsyncMode mode)
        {
            if (mode == AsyncMode.CallerContext && SynchronizationContext.Current != null)
            {
                return TaskScheduler.FromCurrentSynchronizationContext();
            }
            return TaskScheduler.Default;
        }
    }
}
