using System;
using System.Collections.Generic;

namespace KejUtils.UnblockedLocks
{
    /// <summary>
    /// Helper object that manages an individual ILockHolder.
    /// </summary>
    internal class LockHolderHelper : IDisposable
    {
        internal LockHolderHelper(ILockHolder holder, ThreadLockGroup parentGroup)
        {
            this.holder = holder;
            this.subPriority = DateTime.UtcNow;
            this.group = parentGroup;
        }
        /// <summary>
        /// Task this class manages
        /// </summary>
        internal ILockHolder holder;
        /// <summary>
        /// LockGroup for this task
        /// </summary>
        internal ThreadLockGroup group;
        /// <summary>
        /// Second priority if ILockHolder's own priority match up. Earlier tasks get higher priority.
        /// </summary>
        private readonly DateTime subPriority;
        /// <summary>
        /// List of subgroups that this task has taken the lock for.
        /// </summary>
        internal List<ThreadLockGroup> ownedSubgroups;
        /// <summary>
        /// List of tasks that this task has interrupted and notified.
        /// </summary>
        internal List<LockHolderHelper> notifiedTasks;
        /// <summary>
        /// Flag for LockHolderHelper's state. Set to false once GetLocks is finished.
        /// </summary>
        internal bool ThrowOnInterrupt = false;

        /// <summary>
        /// Set during LockThenRun's getLocks to quit instead of calling useLocks.
        /// </summary>
        internal bool Cancel = false;
        /// <summary>
        /// If true, an interruption during LockResource will return false (It will not throw an exception to restart
        /// GetLocks).
        /// Defaults to false.
        /// </summary>
        internal bool ReturnOnDeadlock = false;
        /// <summary>
        /// If this is left as false for the interrupted lock during both InterruptOtherTask and InterruptedByTask,
        /// the interrupted lock will be marked as interrupted and respond according to ReturnOnDeadlock (or the
        /// equivalent bool argument) when it next gets priority. If every call to both InterruptOtherTask or
        /// InterruptedByTask sets this true for the interrupted lock, the interrupted lock will finish the LockResource
        /// call with control of the resource (when it eventually gets the lock).
        /// </summary>
        internal bool IgnoreInterrupt = false;

        /// <summary>
        /// </summary>
        /// <param name="otherTask"></param>
        /// <returns>Positive if this task has priority. Negative if other task has priority. 0 if the tasks have the same priority.</returns>
        internal int ComparePriority(LockHolderHelper otherTask)
        {
            if (otherTask == this) return 0;
            int priorityCompare = holder.CompareLockPriority(otherTask.holder);
            if (priorityCompare != 0) return priorityCompare;
            long ticks = (otherTask.subPriority - subPriority).Ticks;
            if (ticks != 0)
            {
                return (ticks > 0 ? 1 : -1);
            }
            return 0;
        }

        /// <summary>
        /// Dispose of this lock.
        /// </summary>
        public void Dispose()
        {
            group.DisposeOf(this);
        }

    }
}
