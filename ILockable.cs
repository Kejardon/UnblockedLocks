

namespace KejUtils.UnblockedLocks
{
    /// <summary>
    /// A resource that can be locked by a task.
    /// </summary>
    public interface ILockable
    {
        /// <summary>
        /// Object to lock on when reading or writing data this object/lock is responsible for. Must return the same
        /// object every time, and never be null.
        /// When this mutex is available to UnblockedLocks to be locked, nothing else must lock on this mutex.
        /// Must be implemented by class.
        /// Two simple and likely options:
        /// public object LockMutex { get; private set; } = new object();
        /// public object LockMutex { get => this; }
        /// </summary>
        object LockMutex { get; }
        /// <summary>
        /// Current group of Locks that contains this Lockable object. Entirely managed by the UnblockedLocks library.
        /// Implementing classes should just have a simple getter/setter for this.
        /// </summary>
        ThreadLockGroup CurrentLock { get; set; }
    }
}
