using PrefixClassName.MsTest;
using Shouldly;

namespace Singulink.Threading.Tests;

[PrefixTestClass]
public class KeyLockTests
{
    private const string Key = "key";

    [TestMethod]
    public void Default_IsDefault_AndMembersThrowNotInitialized()
    {
        var keyLock = default(KeyLock<string>);

        keyLock.IsDefault.ShouldBeTrue();

        Should.Throw<InvalidOperationException>(() => _ = keyLock.Key);
        Should.Throw<InvalidOperationException>(() => _ = keyLock.Parent);
        Should.Throw<InvalidOperationException>(() => _ = keyLock.IsDisposed);
        Should.Throw<InvalidOperationException>(() => keyLock.Dispose());
    }

    [TestMethod]
    public void Acquired_ExposesKeyAndParent()
    {
        var locker = new KeyLocker<string>();
        var keyLock = locker.Lock(Key);

        keyLock.IsDefault.ShouldBeFalse();
        keyLock.IsDisposed.ShouldBeFalse();
        keyLock.Key.ShouldBe(Key);
        keyLock.Parent.ShouldBeSameAs(locker);

        keyLock.Dispose();
    }

    [TestMethod]
    public void Dispose_SecondCallIsNoOp()
    {
        var locker = new KeyLocker<string>();
        var keyLock = locker.Lock(Key);

        keyLock.Dispose();
        keyLock.IsDisposed.ShouldBeTrue();

        // A second dispose must not release the key's entry again (another holder could own it by then).
        keyLock.Dispose();
        keyLock.IsDisposed.ShouldBeTrue();

        locker.IsLocked(Key).ShouldBeFalse();
    }

    [TestMethod]
    public void Disposed_MembersThrowObjectDisposed()
    {
        var locker = new KeyLocker<string>();
        var keyLock = locker.Lock(Key);
        keyLock.Dispose();

        Should.Throw<ObjectDisposedException>(() => _ = keyLock.Key);
        Should.Throw<ObjectDisposedException>(() => _ = keyLock.Parent);
    }
}
