
using System;
using System.Collections.Generic;
using System.Threading;

namespace KejUtils.UnblockedLocks
{
    /// <summary>
    /// Helper object that manages all of a thread's locks.
    /// 
    /// Ideally this should be used by threads. This can be used by tasks, but it is important that the tasks do NOT share threads.
    /// </summary>
    public class ThreadLockGroup
    {
        /// <summary>
        /// Current ThreadLockGroup being used by a thread. Represents that this thread owns this lock.
        /// This ensures that child tasks will use the same lock group.
        /// </summary>
        [ThreadStatic]
        internal static ThreadLockGroup CurrentLockGroup;

        /// <summary>
        /// When two threads manage to have identical priority, they can get a priority from this int with an atomic inc-and-read action.
        /// (should be extraordinarily rare, but nothing technically stops it from happening)
        /// </summary>
        internal static int PriorityChooser = 0;


        internal ThreadLockGroup()
        {
        }

        #region Status variables
        /// <summary>
        /// Monitored object for changes in status of this lock group. See other fields in this region for more detail:
        /// taskQueue, waitingOn.
        /// </summary>
        internal readonly object statusMutex = new object();
        /// <summary>
        /// List of LockableLocks that have this as their primary lock group.
        /// Because these will all be from the same thread, this list is essentially a queue; task 0 is the root / parent call to LockThenRun,
        /// and each later task is a nested call.
        /// taskQueue.Count can be freely checked, if it is 0, this ThreadLockGroup is expired. Other values mean it is active
        /// and may not be threadsafe (value may have changed since read if the lock on this.statusMutex is not held).
        /// </summary>
        internal readonly List<LockHolderHelper> taskQueue = new List<LockHolderHelper>();
        /// <summary>
        /// Currently ThreadLockGroup that has a resource this group is waiting on. Used to detect lock loops/deadlocks.
        /// this.waitingOn may be set to an object with a lock on this.statusMutex
        /// this.waitingOn may be set to null with a lock on this.waitingOn.statusMutex
        /// </summary>
        internal ThreadLockGroup waitingOn;
        #endregion
        /// <summary>
        /// The ThreadLockGroup to return control to when this ThreadLockGroup is done.
        /// This group will also have control of all the parent's locks while it is running, and leave the CurrentLock
        /// of those ILockables alone. Newly added locks will be assigned to this ThreadLockGroup. Interruptions and similar
        /// things will still treat this group and the previous group as part of the same stack of tasks.
        /// </summary>
        internal ThreadLockGroup previousLockGroup;
        /// <summary>
        /// Flag for if this group has been interrupted during the current LockResource call.
        /// This is only set while waitingOn is set, and only checked when waitingOn is null, so it does not directly need
        /// a lock to work correctly between threads
        /// </summary>
        internal bool wasInterrupted = false;
        /// <summary>
        /// Final priority authority if everything else fails, assigned from static incremented variable.
        /// -1 if not set yet, otherwise will only be set once and never modified again later.
        /// Lower numbers = higher priority.
        /// </summary>
        internal volatile int subSubPriorityValue = -1;
        internal int subSubPriority { get {
                if (previousLockGroup != null) return previousLockGroup.subSubPriority;
                return subSubPriorityValue; } set {
                if (previousLockGroup != null) previousLockGroup.subSubPriority = value;
                else subSubPriorityValue = value; ;
            } }
        /// <summary>
        /// List of groups that are currently using locks from this group.
        /// </summary>
        internal List<ThreadLockGroup> groupsBorrowingThisGroup;

        /// <summary>
        /// Checks of this lock contains the other lock. Should only be called by this own thread while it's active, assumes
        /// there's no race conditions.
        /// </summary>
        /// <param name="other"></param>
        /// <param name="recursive">True if subgroups of subgroups should be searched</param>
        /// <returns></returns>
        private bool Contains(ThreadLockGroup other)
        {
            if (other == this)
            {
                return true;
            }
            foreach (LockHolderHelper nextTask in this.taskQueue)
            {
                if (nextTask.ownedSubgroups == null)
                {
                    continue;
                }
                foreach (ThreadLockGroup nextGroup in nextTask.ownedSubgroups)
                {
                    if (nextGroup.Contains(other)) return true;
                }
            }
            if (previousLockGroup != null)
                return previousLockGroup.Contains(other);
            return false;
        }

        /// <summary>
        /// Make sure a task contains a subgroup. Does nothing if it already does.
        /// Handles interrupts for tasks in that subgroup, and also interrupts for other groups using that subgroup.
        /// </summary>
        /// <param name="currentTask">Task to add the subgroup to. Must be part of this lock group.</param>
        /// <param name="newSubgroup">Subgroup to try to add to the task.</param>
        private void AddToLockableLock(LockHolderHelper currentTask, ThreadLockGroup newSubgroup)
        {
            //Mark this as holding other locks
            if (currentTask.ownedSubgroups == null)
            {
                currentTask.ownedSubgroups = new List<ThreadLockGroup>();
            }
            currentTask.ownedSubgroups.Add(newSubgroup);
            //Add self to original lock owner's chain
            if (newSubgroup.groupsBorrowingThisGroup == null)
            {
                newSubgroup.groupsBorrowingThisGroup = new List<ThreadLockGroup>();
            }
            newSubgroup.groupsBorrowingThisGroup.Add(this);

            //Tell the most recent owner (before ourselves) that we're interrupting it
            //DONE now: On second thought, makes more sense to notify all threads we're interrupting. Only the most recent
            //task of each thread really needs to be notified.
            if (currentTask.notifiedTasks == null)
            {
                currentTask.notifiedTasks = new List<LockHolderHelper>();
            }
            LockHolderHelper otherTask;
            for (int groupToNotifyIndex = newSubgroup.groupsBorrowingThisGroup.Count - 2; groupToNotifyIndex >= 0; groupToNotifyIndex--)
            {
                ThreadLockGroup groupToNotify = newSubgroup.groupsBorrowingThisGroup[groupToNotifyIndex];
                otherTask = groupToNotify.taskQueue[^1];
                currentTask.notifiedTasks.Add(otherTask);
                //Possible small future optimization, but unlikely because it gets a little complicated and changes behavior:
                //Maybe delay notifying if current task is in its first GetLocks call, until it reaches UseLocks.
                otherTask.IgnoreInterrupt = false;
                currentTask.holder.InterruptOtherTask(new InterruptStruct(currentTask, otherTask.holder, false));
                otherTask.holder.InterruptedByTask(new InterruptStruct(otherTask, currentTask.holder, false));
                if (!otherTask.IgnoreInterrupt) otherTask.group.wasInterrupted = true;
            }
            otherTask = newSubgroup.taskQueue[^1];
            currentTask.notifiedTasks.Add(otherTask);
            //Possible small future optimization, but unlikely because it gets a little complicated and changes behavior:
            //Maybe delay notifying if current task is in its first GetLocks call, until it reaches UseLocks.
            otherTask.IgnoreInterrupt = false;
            currentTask.holder.InterruptOtherTask(new InterruptStruct(currentTask, otherTask.holder, false));
            otherTask.holder.InterruptedByTask(new InterruptStruct(otherTask, currentTask.holder, false));
            if (!otherTask.IgnoreInterrupt) otherTask.group.wasInterrupted = true;
        }

        /// <summary>
        /// Get the highest priority lock in this lock group. May fail if the lock isn't held and return null.
        /// </summary>
        /// <returns>The lock manage for the single highest priority task if one is found. Else null
        /// if another thread is active and interrupting the lookup.</returns>
        private LockHolderHelper HighestPriority(int maxSize)
        {
            try
            {
                LockHolderHelper highestPriority = this.taskQueue[0];
                for (int i = 1; i < maxSize; i++)
                {
                    LockHolderHelper otherTask = taskQueue[i];
                    if (highestPriority.ComparePriority(otherTask) < 0)
                        highestPriority = otherTask;
                }
                if (previousLockGroup != null)
                {
                    LockHolderHelper previousHighest = previousLockGroup.HighestPriority(previousLockGroup.taskQueue.Count);
                    if (previousHighest == null) return null; //Some really screwy race condition must have happened for *this* to be null, but it's possible.
                    if (highestPriority.ComparePriority(previousHighest) < 0)
                        highestPriority = previousHighest;
                }
                if (taskQueue.Count != maxSize)
                    return null;
                return highestPriority;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Compare the current known highest priority to the other group's priority. Finds and returns the highest priority task.
        /// </summary>
        /// <param name="previousHighest"></param>
        /// <param name="otherGroup"></param>
        /// <param name="otherGroupSize"></param>
        /// <returns></returns>
        private static LockHolderHelper ChooseHighestPriority(LockHolderHelper previousHighest, ThreadLockGroup otherGroup, int otherGroupSize)
        {
            LockHolderHelper otherHighest = otherGroup.HighestPriority(otherGroupSize);
            if (otherHighest == null) return null;
            int priority = previousHighest.ComparePriority(otherHighest);
            if (priority == 0)
            {
                if (otherGroup.subSubPriority == -1)
                {
                    lock (otherGroup.statusMutex)
                    {
                        if (otherGroup.subSubPriority == -1)
                            otherGroup.subSubPriority = Interlocked.Increment(ref ThreadLockGroup.PriorityChooser);
                    }
                }
                if (previousHighest.group.subSubPriority == -1)
                {
                    //Previous task doesn't have a priority yet and will get a new value later, which must be higher than otherGroup's value.
                    //So the other task has priority.
                    priority = -1;
                }
                else
                {
                    priority = otherHighest.group.subSubPriority - previousHighest.group.subSubPriority;
                }
            }
            if (priority < 0)
                return otherHighest;
            return previousHighest;
        }

        /// <summary>
        /// Get the lock for the requested resource. This may block if the resource is locked by another thread.
        /// This will avoid deadlocks in case of multiple threads locking the same resources; one thread will allow
        /// another thread to take its resources and interrupt it depending on task priority.
        /// </summary>
        /// <param name="resource">Resource to get the lock on</param>
        /// <param name="newLock"></param>
        /// <param name="getLocksReturns">If this is during GetLocks, true will return on deadlock while false will restart GetLocks.</param>
        internal GetLocksStruct.AddResult LockResource(ILockable resource, LockHolderHelper newLock, bool getLocksReturns, bool throwOnInterrupt)
        {
            ThreadLockGroup otherGroup = resource.CurrentLock;

            //Check if we already have the lock
            if (otherGroup != null)
            {
                if (this.Contains(otherGroup))
                    return GetLocksStruct.AddResult.OldLock;
            }

            try
            {
                LockHolderHelper highestPriority = null;
            restartLockResource:
                //Check if we can trivially get the lock
                lock (resource.LockMutex)
                {
                    otherGroup = resource.CurrentLock;
                    if (otherGroup == null || otherGroup.taskQueue.Count == 0)
                    {
                        //If resource has never been locked or has an expired lock, we have the status locked and can claim it ourselves
                        resource.CurrentLock = this;
                        return GetLocksStruct.AddResult.NewLock;
                    }
                }

                //Another group has the lock we want.
                List<ThreadLockGroup> otherGroupQueue = new List<ThreadLockGroup>();
                otherGroupQueue.Add(otherGroup);

                lock (this.statusMutex)
                {
                    this.waitingOn = otherGroup;
                }
                highestPriority ??= this.HighestPriority(taskQueue.Count);
                int otherGroupSize;
                ThreadLockGroup nextGroup;
            checkWaitingOn:
                //Check the group that originally got the lock
                lock (otherGroup.statusMutex)
                {
                    //Is that group also waiting on another group for a lock?
                    nextGroup = otherGroup.waitingOn;
                    while (nextGroup == null)
                    {
                        //Other group expired. Try to get the lock again normally.
                        if (otherGroup.taskQueue.Count == 0)
                        {
                            this.waitingOn = null;
                            if (wasInterrupted)
                            {
                                wasInterrupted = false;
                                if (getLocksReturns) return GetLocksStruct.AddResult.FailedToGetLock;
                                if (throwOnInterrupt)
                                {
                                    throw new DeadlockResetException();
                                }
                            }

                            goto restartLockResource;
                        }
                        //Other group is active. Have to wait for that group first.
                        Monitor.Wait(otherGroup.statusMutex);
                        nextGroup = otherGroup.waitingOn;
                    }
                    //Other group wants a lock from nextGroup. Time to figure out how that's going.
                    otherGroupSize = otherGroup.taskQueue.Count;
                }
                otherGroupQueue.Add(nextGroup);
                highestPriority = ChooseHighestPriority(highestPriority, otherGroup, otherGroupSize);
                if (highestPriority == null)
                    //There is an active thread *doing* things, wait until that thread does stuff
                    goto waitOnActiveThread;

                while (true)
                {
                    nextGroup = otherGroupQueue[^1];
                    ThreadLockGroup nextNextGroup;
                    lock (nextGroup.statusMutex)
                    {
                        //Make sure lock is up to date.
                        if (nextGroup.taskQueue.Count == 0 || otherGroupQueue[^2].waitingOn != nextGroup)
                        {
                            //Out of date, meaning there is an active thread. We can wait until the group we're waiting
                            //on finishes, or another thread tells us we're the highest priority thread.
                            goto waitOnActiveThread;
                        }
                        //Lock isn't out of date. Check how this group is doing.
                        nextNextGroup = nextGroup.waitingOn;
                        otherGroupSize = nextGroup.taskQueue.Count;
                    }
                    if (nextNextGroup == null)
                    {
                        //This group is active. Continue and wait on otherGroup.
                        goto waitOnActiveThread;
                    }
                    if (this.Contains(nextGroup))
                    {
                        //There is a loop/deadlock, eventually otherGroup is waiting on this group.
                        if (this.Contains(highestPriority.group))
                        {
                            //We are the highest priority thread found. We get to take a new subgroup and continue getting locks.
                            goto setupSubgroup;
                        }
                        //else wake up the highestPriority thread.
                        goto wakeHighestPriority;
                    }
                    foreach (ThreadLockGroup otherWaitingGroup in otherGroupQueue)
                    {
                        if (otherWaitingGroup == nextNextGroup)
                        {
                            //There's a circular loop, but this group's not in it. Consider it the same as an active thread:
                            //someone in the loop should be realizing soon it's a deadlock and resolving it, but it won't
                            //immediately be waking us up because we're not in the deadlock to take control of it.
                            goto waitOnActiveThread;
                        }
                    }
                    //nextGroup is also waiting on another group of unknown status. Check the next task/group it is waiting on.

                    highestPriority = ChooseHighestPriority(highestPriority, nextGroup, otherGroupSize);
                    if (highestPriority == null)
                        //There is an active thread *doing* things, wait until that thread does stuff
                        goto waitOnActiveThread;
                    otherGroupQueue.Add(nextNextGroup);
                    continue;
                }

            //Code never falls into here, this requires a goto
            wakeHighestPriority:
                //We're in a deadlock and not the highest priority thread. Make sure the highest priority thread is woken up.
                ThreadLockGroup highestPriorityIsWaitingOn;
                lock (highestPriority.group.statusMutex)
                {
                    //Get that thread's wakeup signal
                    highestPriorityIsWaitingOn = highestPriority.group.waitingOn;
                }

                // If this is out of date, the highest priority thread already did stuff and we didn't need to wake it up anyways.
                if (highestPriorityIsWaitingOn != null) lock (highestPriorityIsWaitingOn.statusMutex)
                    {
                        // More sanity checking to make sure nothing crazy happened in between our locks
                        if (highestPriority.group.waitingOn == highestPriorityIsWaitingOn //Group still wants the same locks
                            && highestPriority.group.taskQueue.Contains(highestPriority)) //Locks still wanted for the same reason
                        {
                            //Tell that thread to wake up and do stuff.
                            //setting waitingOn to null indicates it's the highest priority thread, so it can immediately go do things
                            //instead of repeating all the lookups we just did
                            highestPriority.group.waitingOn = null;
                            Monitor.PulseAll(highestPriorityIsWaitingOn.statusMutex);

                        }
                        //else //there is another active thread and lots of things have been happening, so
                        //    goto waitOnActiveThread; //which is the same as fallilng through now
                    }
                waitOnActiveThread:
                lock (otherGroup.statusMutex)
                {
                    //Doublecheck to make sure things haven't changed since we last had a lock
                    if (this.waitingOn == otherGroup && otherGroup.taskQueue.Count > 0)
                        //Nobody has tried to wake us up and the group we're immediately waiting on isn't expired
                        //we can sleep until someone else does something
                        Monitor.Wait(otherGroup.statusMutex);
                }
                //Check if we were told we're the highest priority group. The only way waitingOn can be set null at this point in
                //code is from another thread, and another thread only sets waitingOn to null if this is the highest priority group.
                if (this.waitingOn == null)
                {
                    otherGroup = resource.CurrentLock;
                    goto setupSubgroupAlreadyCleared;
                }
                //Something else has changed, go see what has changed and if we can act now.
                goto checkWaitingOn;
            }
            finally //catch (Exception e)
            {
                //I don't think an exception is possible without someone violating ILockHolder's contract,
                //but in case that happens, we can clean up here so if nothing else breaks things can recover correctly
                ThreadLockGroup oldWaitingOn = this.waitingOn;
                if (oldWaitingOn != null)
                {
                    lock (oldWaitingOn.statusMutex)
                    {
                        this.waitingOn = null;
                    }
                }
                //TODO: Strongly worded console message criticizing the programmer
                //throw e;
            }
        //Code never falls into here, this requires a goto
        setupSubgroup:
            lock (otherGroup.statusMutex)
            {
                this.waitingOn = null;
            }
        setupSubgroupAlreadyCleared:
            //This is the highest priority thread. Setup subgroups.
            AddToLockableLock(newLock, otherGroup);
            if (this.wasInterrupted)
            {
                this.wasInterrupted = false;
                if (getLocksReturns) return GetLocksStruct.AddResult.FailedToGetLock;
                if (throwOnInterrupt)
                {
                    throw new DeadlockResetException();
                }
            }
            return GetLocksStruct.AddResult.TransferredLock;

        }

        /// <summary>
        /// Add a task to the task queue.
        /// </summary>
        /// <param name="newTask"></param>
        internal LockHolderHelper AddTask(ILockHolder newTask)
        {
            LockHolderHelper newLock = new LockHolderHelper(newTask, this);
            taskQueue.Add(newLock);
            return newLock;
        }

        /// <summary>
        /// Check if the current thread already has a lock.
        /// </summary>
        /// <returns></returns>
        public static bool HasALock()
        {
            return CurrentLockGroup != null;
        }

        /// <summary>
        /// Dispose of this lock.
        /// </summary>
        internal void DisposeOf(LockHolderHelper oldLock)
        {
            //sanity check if oldLock == taskQueue.Last ?
            if (oldLock.ownedSubgroups != null)
            {
                for (int i = oldLock.ownedSubgroups.Count - 1; i >= 0; i--)
                {
                    ThreadLockGroup group = oldLock.ownedSubgroups[i];
                    //Tell the original owner of the lock we're done with it
                    group.groupsBorrowingThisGroup.RemoveAt(group.groupsBorrowingThisGroup.Count - 1);
                }
                if (oldLock.notifiedTasks != null)
                {
                    for (int i = oldLock.notifiedTasks.Count -1; i >= 0; i--)
                    {
                        //Get the next most recent active owner of the lock
                        LockHolderHelper otherGroup = oldLock.notifiedTasks[i];
                        ILockHolder otherHolder = otherGroup.holder;
                        //Tell the next owner of the lock we're done interrupting it
                        otherGroup.IgnoreInterrupt = false;
                        oldLock.holder.InterruptOtherTask(new InterruptStruct(oldLock, otherHolder, true));
                        otherHolder.InterruptedByTask(new InterruptStruct(otherGroup, oldLock.holder, true));
                        if (!otherGroup.IgnoreInterrupt) otherGroup.group.wasInterrupted = true;
                    }
                }
            }
            lock (statusMutex)
            {
                taskQueue.RemoveAt(taskQueue.Count - 1);
                if (taskQueue.Count == 0)
                {
                    ThreadLockGroup.CurrentLockGroup = previousLockGroup;
                    if (previousLockGroup != null)
                    {
                        previousLockGroup.waitingOn = null;
                    }
                    Monitor.PulseAll(statusMutex);
                    //Maybe TODO: Other memory freeing things? Since there may be stale references to LockableLockGroups
                    //it might be nice if expired LockableLockGroups didn't take much memory.
                    //groupsBorrowingThisGroup can be set to null, taskQueue can be replaced with static empty list
                }
            }

        }
    }


}