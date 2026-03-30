using Blizztrack.Shared.Extensions;
using static Blizztrack.Shared.Extensions.BinarySearchExtensions;

namespace Minimaps.Tests;

public class BinarySearchTests
{
    /// <summary>
    /// BinarySearchBy was doing an invalid memory access from a mismatch between documented behaviour & actual behavior 
    /// (searches that have no matching results were returning the last index + 1, not -1)
    /// This will catch if that regresses.
    /// </summary>
    [Fact]
    public void BinarySearchBy_Predicate_NotFound_PastEnd_ReturnsNegative()
    {
        var array = new[] { 10, 20, 30, 40, 50 };

        var result = array.BinarySearchBy((ref int entry) => (entry - 60).ToOrdering());

        Assert.Equal(-1, result);
    }

    [Fact]
    public void BinarySearchBy_Predicate_NotFound_Middle_ReturnsNegative()
    {
        var array = new[] { 10, 20, 40, 50 };

        var result = array.BinarySearchBy((ref int entry) => (entry - 30).ToOrdering());

        Assert.Equal(-1, result);
    }

    [Fact]
    public void BinarySearchBy_Predicate_Found_ReturnsCorrectIndex()
    {
        var array = new[] { 10, 20, 30, 40, 50 };

        var result = array.BinarySearchBy((ref int entry) => (entry - 30).ToOrdering());

        Assert.Equal(2, result);
    }
}
