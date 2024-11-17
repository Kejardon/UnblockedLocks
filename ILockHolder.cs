﻿using System;
using System.Collections.Generic;

namespace KejUtils.UnblockedLocks
{
    /// <summary>
    /// Interface for tasks that will get a collection of locks to read or modify the program state.
    /// Responsible for handling complications in case of deadlocks. It is valid to have only trivial
    /// implementations for this interface, if lock contentions don't need special handling.
    /// </summary>
    public interface ILockHolder
    {
        /// <summary>
        /// Priority for a lock. Only used when deadlock is hit, otherwise active threads keep priority.
        /// Comparisons should generally work as IComparable's contract; if this has higher priority it should return
        /// a positive number. If the other lockholder has higher priority, this should return a negative number.
        /// If both are equivalent or a different comparison is needed, this should return 0.
        /// </summary>
        int CompareLockPriority(ILockHolder other);

        /// <summary>
        /// Optional action when this task is interrupting another task.
        /// This is called before InterruptedByTask on the other task. This is called twice, once before this task
        /// finishes its UseLocks block and once after.
        /// </summary>
        /// <param name="otherHolder">The task being interrupted.</param>
        /// <param name="finishedUseLocks">False when interruption begins, true when this task is finished.</param>
        void InterruptOtherTask(InterruptStruct interruptStruct);

        /// <summary>
        /// Optional response to this task being interrupted by another task.
        /// This is called after InterruptOtherTask from the other task. This is called twice, once before the other
        /// task finishes its UseLocks block and once after.
        /// </summary>
        /// <param name="otherHolder">The task interrupting this task.</param>
        /// <param name="finishedUseLocks">False when interruption begins, true when otherHolder is finished.</param>
        void InterruptedByTask(InterruptStruct interruptStruct);
    }

    /// <summary>
    /// Event helper object for when one task is interrupted by another.
    /// </summary>
    public struct InterruptStruct
    {
        internal InterruptStruct(LockHolderHelper myLock, ILockHolder otherHolder, bool finishedUseLocks)
        {
            lockableLock = myLock;
            OtherHolder = otherHolder;
            FinishedUseLocks = finishedUseLocks;
        }
        internal LockHolderHelper lockableLock;
        /// <summary>
        /// The other task.
        /// </summary>
        public readonly ILockHolder OtherHolder;
        /// <summary>
        /// False if the interruption has just started. True if the interrupting task is finished and returning control
        /// of the locks to the other task.
        /// </summary>
        public readonly bool FinishedUseLocks;

        /// <summary>
        /// Set during this task's LockThenRun's getLocks to quit instead of calling useLocks. This has no effect outside
        /// of that window.
        /// </summary>
        public bool Cancel { get { return lockableLock.Cancel; } set { lockableLock.Cancel = value; } }
    }

    /// <summary>
    /// Event helper object when a task is acquiring a group of locks.
    /// </summary>
    public struct GetLocksStruct
    {
        internal GetLocksStruct(LockHolderHelper myLock, bool throwOnInterrupt)
        {
            lockableLock = myLock;
            ThrowOnInterrupt = throwOnInterrupt;
        }
        internal LockHolderHelper lockableLock;
        private readonly bool ThrowOnInterrupt;


        /// <summary>
        /// The 4 ordinary results of attempting to lock an object.
        /// During GetLocks, an exception may also be thrown which is not covered by these results.
        /// </summary>
        public enum AddResult
        {
            NewLock, //The object was previously not locked (or the previous lock has completely finished)
            OldLock, //The object was already locked by this thread
            TransferredLock, //The object is locked by another thread in a deadlock, but this thread has the highest priority and can use it now
            FailedToGetLock, //The object is locked by another thread in a deadlock, and another thread had higher priority causing this thread to get interrupted. Only used when ReturnOnDeadlock is true.
        }

        /// <summary>
        /// Get the lock for the requested resource. This may block if the resource is locked by another thread.
        /// ReturnOnDeadlock will determine behavior on deadlock during GetLocks.
        /// </summary>
        /// <param name="resource">Resource to acquire the lock for.</param>
        public AddResult LockResource(ILockable resource)
        {
            //This can succeed or be interrupted.
            return lockableLock.group.LockResource(resource, lockableLock, lockableLock.ReturnOnDeadlock, ThrowOnInterrupt);
        }
        /// <summary>
        /// Get the lock for the requested resource. This may block if the resource is locked by another thread.
        /// </summary>
        /// <param name="resource">Resource to acquire the lock for.</param>
        /// <param name="returnOnDeadlock">True if a deadlock interruption should return, false if it should restart
        /// the getLocks call instead.</param>
        public AddResult LockResource(ILockable resource, bool returnOnDeadlock)
        {
            //This can succeed or be interrupted.
            return lockableLock.group.LockResource(resource, lockableLock, returnOnDeadlock, ThrowOnInterrupt);
        }

        /// <summary>
        /// Set during ILockHolder.LockThenRun's getLocks to quit instead of calling useLocks. Has no effect during an
        /// ILockHolder.GetLocks call.
        /// </summary>
        public bool Cancel { get { return lockableLock.Cancel; } set { lockableLock.Cancel = value; } }

    }

    public static partial class Extensions
    {
        /// <summary>
        /// Uses an ILockHolder to manage a task - organizes locks with a functional approach. Gets the locks required
        /// for the task using getLocks, then executes the task with useLocks.
        /// </summary>
        /// <param name="holder">ILockHolder to manage the task.</param>
        /// <param name="getLocks">Function to get the locks for this task. This must not modify your program state.</param>
        /// <param name="useLocks">Function to use the locks for this task.</param>
        /// <param name="getLockReturnOnDeadlock">Default returnOnDeadlock argument for GetLocksStructure.LockResource
        /// during getLocks.</param>
        /// <param name="dynamicLocks">True if changes to the program state may affect getLocks. False if getLocks will always
        /// get the same locks regardless of program state.</param>

        public static bool LockThenRun(this ILockHolder holder, Action<GetLocksStruct> getLocks, Action useLocks, bool getLockReturnOnDeadlock = false, bool dynamicLocks = true)
        {
            if (holder == null) throw new NullReferenceException();

            LockHolderHelper lockableLock = InitLockGroup(holder);
            lockableLock.ReturnOnDeadlock = getLockReturnOnDeadlock;

            try
            {
                //NOTE: I'm considering passing the result of GetLocksInternal to useLocks.
                //If I end up with a reason to give useLocks any arguments, I definitely should include that.
                GetLocksInternal(getLocks, new GetLocksStruct(lockableLock, dynamicLocks));

                if (lockableLock.Cancel)
                    return false;

                useLocks();
            }
            finally
            {
                lockableLock.Dispose();
            }
            return true;
        }

        /// <summary>
        /// Uses an ILockHolder to manage a task. This should be called inside of a using block. Organizes locks
        /// inside of a block of code.
        /// Enables calling ILockHolder.GetLocks and ILockHolder.GetSingleLock inside of the using block.
        /// </summary>
        /// <param name="lockHolder">ILockHolder to manage the task.</param>
        /// <returns>Using to dispose of to indicate the locks are no longer in use.</returns>
        public static IDisposable StartLockBlock(this ILockHolder lockHolder)
        {
            if (lockHolder == null) throw new NullReferenceException();

            LockHolderHelper lockableLock = InitLockGroup(lockHolder);

            return lockableLock;
        }
        /// <summary>
        /// Get a group of locks at once. This must be called after StartLockBlock
        /// </summary>
        /// <param name="lockHolder">ILockHolder to manage the task.</param>
        /// <param name="getLocks">Function to get the locks for this task. This must not modify your program state.</param>
        /// <param name="getLockReturnOnDeadlock">Default returnOnDeadlock argument for GetLocksStructure.LockResource</param>
        /// <param name="dynamicLocks">True if changes to the program state may affect getLocks. False if getLocks will always
        /// get the same locks regardless of program state.</param>
        /// <returns>True if this was interrupted while getting locks</returns>
        public static bool GetLocks(this ILockHolder lockHolder, Action<GetLocksStruct> getLocks, bool getLockReturnOnDeadlock = false, bool dynamicLocks = true)
        {
            GetLocksStruct lockStruct = ValidateLockStruct(lockHolder, dynamicLocks);
            lockStruct.lockableLock.ReturnOnDeadlock = getLockReturnOnDeadlock;
            return GetLocksInternal(getLocks, lockStruct);
        }

        private static GetLocksStruct ValidateLockStruct(ILockHolder lockHolder, bool dynamicLocks)
        {
            if (lockHolder == null)
                throw new NullReferenceException();
            List<LockHolderHelper> currentQueue = ThreadLockGroup.CurrentLockGroup.taskQueue;
            LockHolderHelper currentLock = currentQueue[^1];
            if (currentLock.holder != lockHolder)
                throw new InvalidOperationException();
            GetLocksStruct lockStruct = new GetLocksStruct(currentLock, dynamicLocks);
            return lockStruct;
        }

        /// <summary>
        /// Get a single lock. This must be called after StartLockBlock.
        /// Because this gets only a single lock, there is no getLocks function to restart if it is interrupted.
        /// This should be used mainly when a task will need only one lock that will not change even if other tasks
        /// have modified the program state.
        /// </summary>
        /// <param name="lockHolder">ILockHolder to manage the task.</param>
        /// <param name="resource">Resource to acquire the lock for.</param>
        /// <param name="returnOnDeadlock">True if a deadlock interruption should return failure - the caller should check
        /// the result in case of failure.
        /// False to block until the lock is acquired - this should be used mainly when a task will need only one lock
        /// that will not change even if other tasks have modified the program state.</param>
        /// <param name="dynamicLocks">True if changes to the program state may affect getLocks. False if getLocks will always
        /// get the same locks regardless of program state.</param>
        /// <returns>Result of acquiring the lock.</returns>
        public static GetLocksStruct.AddResult GetSingleLock(this ILockHolder lockHolder, ILockable resource, bool returnOnDeadlock = false, bool dynamicLocks = true)
        {
            //Idle thought: This *could* be called from a GetLocks, and it would more or less work correctly,
            //but it would be identical to just calling LockResource on the existing lockStruct.
            //Outside of GetLocks, this functions a little different because it will not throw to restart a GetLocks segment.
            //(inside of GetLocks it can throw, so returnOnDeadlock=false will not work exactly as claimed)
            GetLocksStruct lockStruct = ValidateLockStruct(lockHolder, dynamicLocks);

            return lockStruct.LockResource(resource, returnOnDeadlock);
        }

        //maybe TODO: A timeout on LockThenRun *could* work, as long as it was only used for GetLocks and there wasn't
        //a previous task. Would allow a new anomolous state though, where an expired group has locks being borrowed
        //by a higher priority group.

        private static bool GetLocksInternal(Action<GetLocksStruct> getLocks, GetLocksStruct lockableLock)
        {
            bool anyInterrupts = false;
            lockableLock.lockableLock.ThrowOnInterrupt = true;
            while (true)
            {
                try
                {
                    getLocks(lockableLock);
                    break;
                }
                catch (DeadlockResetException)
                {
                    anyInterrupts = true;
                }
            }
            lockableLock.lockableLock.ThrowOnInterrupt = false;
            return anyInterrupts;
        }

        private static LockHolderHelper InitLockGroup(ILockHolder holder)
        {
            LockHolderHelper lockableLock;
            //PERFORMANCE TODO: Add an option to force a new CurrentLockGroup, use old one as a parent.
            //Set parent lockgroup as waiting on new one, add parent one as owned subgroup in new one, restore parent at ThreadLockGroup.DisposeOf
            //This may improve concurrency if it is reasonable to consider it as a distinct group of locks.

            //Check if this is a subsequent task in a single thread
            if (ThreadLockGroup.CurrentLockGroup != null)
            {
                ThreadLockGroup currentLockGroup = ThreadLockGroup.CurrentLockGroup;
                lock (currentLockGroup.statusMutex)
                {
                    lockableLock = currentLockGroup.AddTask(holder);
                }
            }
            else
            {
                ThreadLockGroup currentLockGroup = new ThreadLockGroup();

                ThreadLockGroup.CurrentLockGroup = currentLockGroup;

                lockableLock = currentLockGroup.AddTask(holder);
            }
            return lockableLock;
        }

        ///NOTE: I'm kind of considering adding a thing to allow
        ///    ILockable myResource = something;
        ///    using(myResource.SingleLock()) { /* use lock here */ }
        ///
        /// The main problem (for users) with this idea is that it *must not* be in a lock context already. If it is, it
        ///should immediately throw an exception instead of trying to get the lock. This might be okay to users though?
        ///
        ///The implementation problem (for me) is that implementing it means allowing a ThreadLockGroup to function
        ///without an ILockHolder.

        ///NOTE: I'm also considering allowing *any* kind of object to be locked, using a ConditionalWeakTable to track
        ///the ThreadLockGroup for that object.

    }
}