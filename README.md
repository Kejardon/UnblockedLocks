# UnblockedLocks
Concurrency API for C# that manages locks in a more dynamic and functional way, and automatically detects and helps resolve deadlocks.

Instead of writing
```
private void DoStuff() {
  lock(myResource) {
    //myResource can safely be used only inside this block
    lock(myOtherResource) { //In order for both to be safely used at the same time, the locks must be nested
      //myOtherResource can safely be used only inside this block
    }
  }
}
```
locks can be used something like this:
```
private void DoStuff() {
  //these can be inside of another function or loop, to be done much more dynamically
  LockResource(myResource);
  LockResource(myOtherResource); //Each individual lock does not create a block
  
  //myResource and myOtherResource can safely be used until the end of this function
}
```

### Design priorities (What use cases make sense for this API)
Programmers may get locks in any order and do not *need* to worry about deadlocks, but should take any convenient steps possible to minimize them still. Programmers do not have to worry at all about locking the same object multiple times. Locks should still ideally be held by tasks for as short of a time as possible to minimize lock contention. Locks are released automatically when tasks finish their useLocks code (so to minimize lock contention, long running tasks should still split their code up into segments, and let go of locks when not actively using them)

### API Use
Users of this API can implement `ILockable` on objects shared by several threads that should be locked (to ensure read/writes to data owned by that object are thread-safe), and `ILockHolder` for tasks that use those objects. Then, use provided static (extension) functions for ILockHolders: `LockThenRun` (to pass in everything at once in a functional manner) or `StartLockBlock` (to run inside of a using block). Those two options work essentially the same, but fit different coding styles. Locks will be held until the end of the outermost `LockThenRun` or `StartLockBlock`, at which point all locks held by a thread are released at once.

During a GetLocks call (either `ILockHolder.LockThenRun`'s `getLocks` argument, or `ILockHolder.GetLocks` inside of a `StartLockBlock`), locks can be acquired by calling the provided `GetLocksStruct`'s `LockResource` on ILockable objects. Program state *must* not be changed during GetLocks calls. If a deadlock occurs during GetLocks, UnblockedLocks may restart the entire GetLocks call to ensure the correct dynamic locks are acquired.

Locks are assumed to be dynamic. This means, the locks acquired during GetLocks will depend on the program state; if another thread modifies the program state, a different set of locks should be acquired. To keep things simple, the first lock should never be dynamic, and later locks should only depend on data safe to use according to earlier locks. **If locks cannot be acquired like this, then at the end of getting locks, the programmer must verify that the locks acquired are still correct, and if not, restart the lock-getting process themselves** (`throw new DeadlockResetException()` will restart the GetLocks context, or you can manually handle the program flow as appropriate). If the locks needed by a task are not dynamic, this is not a concern and the arguments in `ILockHolder.GetLocks` or `ILockHolder.LockThenRun` can be set to false to prevent the framework from restarting the GetLocks context.

During `StartLockBlock`, `ILockHolder.GetSingleLock` can also be called to acquire a lock. This is not recommended if you need to acquire dynamic locks, because you will have to manually check if the locks you need to acquire change.

### Dealing with complications
To keep things simplest, the codeflow for tasks should first get *all* locks that the task will need to execute, then verify that the program state for the task is still valid, then finally execute if it is still valid and not get any more locks in its current lock context. If this approach is followed, ILockHolders should not need to worry about deadlock interruptions and can stub out empty implementations (though, they can still be used to finetune things when deadlocks happen).

This API supports getting locks, using them to read and/or modify the program state, and then getting more locks to do more things with while still holding the first group of locks. This can be done by starting a new task in an older lock context (nested tasks will share control of the locks that their parent task has), or simply by getting more locks with more calls to `ILockHolder.GetLocks` or `ILockHolder.GetSingleLock`. However, this also means the program state can be unexpectedly modified at those points, and require more places where the program state has to be verified in order to ensure tasks are still appropriate to run and are still doing what they should be doing.

ILockHolders provide hooks in case of deadlocks. The hooks will *not* be called simply because a lock that is being acquired is already held by a different thread - they will only be called because of deadlocks. Normal lock contention will work as it always had, with the later thread waiting until the earlier thread finishes using its locks. In the case of nested tasks, the hooks of *all* tasks in a stack will be called. `CompareLockPriority` allows you to choose which task is prioritized in case of deadlocks, but can safely be left as 0 to leave it to the framework to decide. After a prioritized task is chosen, `InterruptOtherEvent` and `InterruptedByEvent` allow tasks to communicate with eachother, so you can do whatever is needed to put things into a safe program state and/or verify if tasks should continue running.
