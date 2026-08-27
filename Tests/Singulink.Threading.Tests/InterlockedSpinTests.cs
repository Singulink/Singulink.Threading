using PrefixClassName.MsTest;
using Shouldly;

namespace Singulink.Threading.Tests;

[PrefixTestClass]
public class InterlockedSpinTests
{
    private string _stringLocation = "a";
    private int _intLocation;

    [TestMethod]
    public void Exchange_ReplacesValueAndReturnsNewValue()
    {
        string result = InterlockedSpin.Exchange(ref _stringLocation, out string original, s => s + "b");

        original.ShouldBe("a");
        result.ShouldBe("ab");
        _stringLocation.ShouldBe("ab");
    }

    [TestMethod]
    public void Exchange_FactoryReturnsSameReference_DoesNotSwap()
    {
        string value = _stringLocation;
        string result = InterlockedSpin.Exchange(ref _stringLocation, out string original, s => s);

        original.ShouldBeSameAs(value);
        result.ShouldBeSameAs(value);
        _stringLocation.ShouldBeSameAs(value);
    }

    [TestMethod]
    public async Task Exchange_Concurrent_AppliesEveryUpdate()
    {
        _stringLocation = string.Empty;

        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() =>
            InterlockedSpin.Exchange(ref _stringLocation, s => s + "x"))));

        _stringLocation.Length.ShouldBe(100);
    }

    [TestMethod]
    public void TryIncrementToMax_IncrementsUntilMax()
    {
        InterlockedSpin.TryIncrementToMax(ref _intLocation, 2, out int newValue).ShouldBeTrue();
        newValue.ShouldBe(1);

        InterlockedSpin.TryIncrementToMax(ref _intLocation, 2).ShouldBeTrue();

        InterlockedSpin.TryIncrementToMax(ref _intLocation, 2, out newValue).ShouldBeFalse();
        newValue.ShouldBe(2);
        _intLocation.ShouldBe(2);
    }

    [TestMethod]
    public void TryIncrementToMax_AboveMax_Fails()
    {
        _intLocation = 5;

        InterlockedSpin.TryIncrementToMax(ref _intLocation, 2, out int newValue).ShouldBeFalse();
        newValue.ShouldBe(5);
        _intLocation.ShouldBe(5);
    }

    [TestMethod]
    public void TryDecrementToMin_DecrementsUntilMin()
    {
        _intLocation = 2;

        InterlockedSpin.TryDecrementToMin(ref _intLocation, 0, out int newValue).ShouldBeTrue();
        newValue.ShouldBe(1);

        InterlockedSpin.TryDecrementToMin(ref _intLocation, 0).ShouldBeTrue();

        InterlockedSpin.TryDecrementToMin(ref _intLocation, 0, out newValue).ShouldBeFalse();
        newValue.ShouldBe(0);
        _intLocation.ShouldBe(0);
    }

    [TestMethod]
    public void TryDecrementToMin_BelowMin_Fails()
    {
        _intLocation = -5;

        InterlockedSpin.TryDecrementToMin(ref _intLocation, 0, out int newValue).ShouldBeFalse();
        newValue.ShouldBe(-5);
        _intLocation.ShouldBe(-5);
    }

    [TestMethod]
    public async Task TryIncrementToMax_Concurrent_StopsExactlyAtMax()
    {
        const int Max = 50;
        int successes = 0;

        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() =>
        {
            if (InterlockedSpin.TryIncrementToMax(ref _intLocation, Max))
                Interlocked.Increment(ref successes);
        })));

        successes.ShouldBe(Max);
        _intLocation.ShouldBe(Max);
    }
}
