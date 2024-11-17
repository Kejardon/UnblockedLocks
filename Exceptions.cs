using System;

namespace KejUtils.UnblockedLocks
{
    /// <summary>
    /// Exception thrown when a deadlock happens and another task modified the program state while this thread
    /// waited, and the intended response is to restart what this thread was doing.
    /// Unravels the thread to the starting spot of the current task's getLocks action so it can restart. This can
    /// be safely thrown manually by a client during a getLocks action to restart the getLocks action.
    /// </summary>
    public class DeadlockResetException : Exception
    {
        public DeadlockResetException()
        {
        }

        public DeadlockResetException(string message)
            : base(message)
        {
        }

        public DeadlockResetException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
}
