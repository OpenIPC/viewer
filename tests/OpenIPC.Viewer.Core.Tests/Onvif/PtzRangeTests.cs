using OpenIPC.Viewer.Core.Onvif;

namespace OpenIPC.Viewer.Core.Tests.Onvif;

// Mapping the UI's normalized input onto whatever range a camera declares.
// Getting this wrong does not throw: the move is clamped, refused, or lands
// somewhere else, and on an asymmetric range an axis the user never touched
// drifts on every step. So the arithmetic is pinned here.
public sealed class PtzRangeTests
{
    [Fact]
    public void OnTheDefaultRangeATranslationPassesThroughUnchanged()
    {
        var range = new PtzRange(-1f, 1f);

        Assert.Equal(0f, range.ScaleTranslation(0f), 5);
        Assert.Equal(0.16f, range.ScaleTranslation(0.16f), 5);
        Assert.Equal(-1f, range.ScaleTranslation(-1f), 5);
    }

    // Degrees, which is what a good number of domes report.
    [Fact]
    public void ADegreeRangeScalesByItsHalfSpan()
    {
        var range = new PtzRange(-180f, 180f);

        Assert.Equal(180f, range.ScaleTranslation(1f), 3);
        Assert.Equal(-90f, range.ScaleTranslation(-0.5f), 3);
        Assert.Equal(0f, range.ScaleTranslation(0f), 3);
    }

    // The two properties an asymmetric range must keep at once: zero stays
    // zero (an untouched axis must not creep), and nothing is ever sent
    // outside the declared bounds — [0, 100] simply cannot go negative, so a
    // negative step clamps to its floor instead of being sent below it.
    [Fact]
    public void AnAsymmetricRangeKeepsZeroAndNeverLeavesItsBounds()
    {
        var range = new PtzRange(0f, 100f);

        Assert.Equal(0f, range.ScaleTranslation(0f), 3);
        Assert.Equal(50f, range.ScaleTranslation(1f), 3);
        Assert.Equal(8f, range.ScaleTranslation(0.16f), 3);
        Assert.Equal(0f, range.ScaleTranslation(-0.16f), 3);   // clamped, not -8
        Assert.Equal(0f, range.ScaleTranslation(-1f), 3);
    }

    [Theory]
    [InlineData(2f, 1f)]
    [InlineData(-2f, -1f)]
    [InlineData(99f, 1f)]
    public void InputBeyondTheUnitIntervalIsClampedBeforeScaling(float input, float expected) =>
        Assert.Equal(expected, new PtzRange(-1f, 1f).ScaleTranslation(input), 5);

    // Absolute zoom is [0, 1] at the UI, not [-1, 1].
    [Fact]
    public void UnitInputMapsOntoTheRangeFromItsFloor()
    {
        var range = new PtzRange(0f, 16f);

        Assert.Equal(0f, range.FromUnit(0f), 3);
        Assert.Equal(8f, range.FromUnit(0.5f), 3);
        Assert.Equal(16f, range.FromUnit(1f), 3);
    }

    [Fact]
    public void ToUnitIsTheInverseOfFromUnit()
    {
        var range = new PtzRange(1f, 32f);

        Assert.Equal(0.25f, range.ToUnit(range.FromUnit(0.25f)), 4);
        Assert.Equal(0f, range.ToUnit(range.Min), 4);
        Assert.Equal(1f, range.ToUnit(range.Max), 4);
    }

    [Fact]
    public void APositionOutsideTheRangeStillReadsAsZeroToOne()
    {
        var range = new PtzRange(0f, 10f);

        Assert.Equal(0f, range.ToUnit(-5f), 4);
        Assert.Equal(1f, range.ToUnit(50f), 4);
    }

    // A camera that reports Min == Max, or reports them backwards, has said
    // nothing usable. Scaling by it would collapse every move onto one value,
    // so the value passes through untouched instead.
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(1f, -1f)]
    public void ARangeThatSaysNothingLeavesTheValueAlone(float min, float max)
    {
        var range = new PtzRange(min, max);

        Assert.False(range.IsValid);
        Assert.Equal(0.5f, range.ScaleTranslation(0.5f), 5);
        Assert.Equal(0.5f, range.FromUnit(0.5f), 5);
        Assert.Equal(0.5f, range.Clamp(0.5f), 5);
    }

    [Fact]
    public void ClampKeepsAValueInsideTheDeclaredRange()
    {
        var range = new PtzRange(-0.5f, 0.5f);

        Assert.Equal(0.5f, range.Clamp(2f), 5);
        Assert.Equal(-0.5f, range.Clamp(-2f), 5);
        Assert.Equal(0.1f, range.Clamp(0.1f), 5);
    }
}
