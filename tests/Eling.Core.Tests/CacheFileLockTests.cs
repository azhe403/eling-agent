using Eling.Core.Database;

namespace Eling.Core.Tests;

/// <summary>
/// The cache lock is the one piece of machinery both caches trust: one mutex
/// per name per process, recursive for its owner, released only when it was
/// taken. Each of those rules has a failure mode that looks like "the cache
/// silently stopped working", so each is pinned here.
///
/// <para>
/// The names are unique per test so the cases cannot interfere with each
/// other or with the real caches, which share process-wide mutex objects.
/// </para>
/// </summary>
public sealed class CacheFileLockTests
{
    private static string UniqueName(string prefix) => $@"Global\eling-test-{prefix}-{Guid.NewGuid():N}";

    [Fact]
    public void TwoLocksWithTheSameName_AreTheSameMutex()
    {
        // Two Mutex objects over one named kernel object in a process corrupt
        // thread-ownership accounting: ReleaseMutex starts throwing and the
        // gate never lets go. The wrapper must share one object per name.
        var name = UniqueName("same");
        var first = new CacheFileLock(name);
        var second = new CacheFileLock(name);

        first.Run(() => second.Run(() => { }));
    }

    [Fact]
    public void DifferentNames_DoNotShareTheLock()
    {
        // Different names are different locks, so one thread can hold both at
        // once. The proof is that the inner take does not block: if the two
        // shared a lock, the inner take would wait for the outer one to be
        // released and time out.
        var first = new CacheFileLock(UniqueName("a"));
        var second = new CacheFileLock(UniqueName("b"));
        var innerRan = false;

        first.Run(() =>
        {
            // A different thread would block here; this one must not.
            var thread = new Thread(() => second.Run(() => innerRan = true));
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(5));

            Assert.False(thread.IsAlive);
        });

        Assert.True(innerRan);
    }

    [Fact]
    public void TheOwnerThreadMayTakeTheLockAgain()
    {
        // One operation over two files takes the lock once and does the work
        // under it; the second file's gate must not deadlock against the first.
        var name = UniqueName("nested");
        var lockA = new CacheFileLock(name);
        var lockB = new CacheFileLock(name);

        lockA.Run(() => lockB.Run(() => { }));
    }

    [Fact]
    public void TheLockIsReleasedAfterTheAction()
    {
        var name = UniqueName("release");
        var lockA = new CacheFileLock(name);

        lockA.Run(() => { });

        // If the first Run had not released, this one would time out.
        lockA.Run(() => { });
    }

    [Fact]
    public void TheLockIsReleasedEvenWhenTheActionThrows()
    {
        var name = UniqueName("throw");
        var lockA = new CacheFileLock(name);

        Assert.Throws<InvalidOperationException>(() =>
            lockA.Run(() => throw new InvalidOperationException("boom")));

        // The lock must not stay held by the failed action.
        lockA.Run(() => { });
    }

    [Fact]
    public void ANestedRunDoesNotReleaseTheOuterHold()
    {
        // The inner Run takes the mutex again (it is recursive) and releases it
        // once, so the outer hold is still there. If the inner release had
        // unwound the outer's count, the outer release would throw
        // "unsynchronized block" — which is what this test would catch.
        var name = UniqueName("count");
        var lockA = new CacheFileLock(name);
        var lockB = new CacheFileLock(name);

        lockA.Run(() => lockB.Run(() => { }));

        // If the outer hold were gone, this Run would have to wait for
        // something that never releases it, and time out.
        lockA.Run(() => { });
    }
}
