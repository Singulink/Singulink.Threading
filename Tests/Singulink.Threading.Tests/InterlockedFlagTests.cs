using PrefixClassName.MsTest;
using Shouldly;

namespace Singulink.Threading.Tests;

[PrefixTestClass]
public class InterlockedFlagTests
{
    private InterlockedFlag _flag;

    [TestMethod]
    public void Default_IsNotSet()
    {
        _flag.IsSet.ShouldBeFalse();
    }

    [TestMethod]
    public void Ctor_InitialState_IsRespected()
    {
        _flag = new InterlockedFlag(true);
        _flag.IsSet.ShouldBeTrue();

        _flag = new InterlockedFlag(false);
        _flag.IsSet.ShouldBeFalse();
    }

    [TestMethod]
    public void TrySet_OnlyFirstCallSucceeds()
    {
        _flag.TrySet().ShouldBeTrue();
        _flag.IsSet.ShouldBeTrue();

        _flag.TrySet().ShouldBeFalse();
        _flag.IsSet.ShouldBeTrue();
    }

    [TestMethod]
    public void TryClear_OnlySucceedsWhenSet()
    {
        _flag.TryClear().ShouldBeFalse();

        _flag.TrySet();
        _flag.TryClear().ShouldBeTrue();
        _flag.IsSet.ShouldBeFalse();

        _flag.TryClear().ShouldBeFalse();
    }

    [TestMethod]
    public void SetAndClear_CanCycle()
    {
        for (int i = 0; i < 3; i++)
        {
            _flag.TrySet().ShouldBeTrue();
            _flag.TryClear().ShouldBeTrue();
        }
    }

    [TestMethod]
    public async Task TrySet_Concurrent_ExactlyOneWinner()
    {
        int winners = 0;

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            if (_flag.TrySet())
                Interlocked.Increment(ref winners);
        })));

        winners.ShouldBe(1);
        _flag.IsSet.ShouldBeTrue();
    }
}
