using Monitor.Data.Repositories;

namespace Monitor.UnitTests.Repositories;

public class OperationalResultTests
{
    [Fact]
    public void Ok_SetsAvailableTrueAndCarriesValue()
    {
        var result = OperationalResult<int>.Ok(42);

        Assert.True(result.Available);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Unavailable_SetsAvailableFalseAndDefaultValue()
    {
        var result = OperationalResult<int>.Unavailable();

        Assert.False(result.Available);
        Assert.Equal(default, result.Value);
    }

    [Fact]
    public void Unavailable_ForReferenceType_HasNullValue()
    {
        var result = OperationalResult<string>.Unavailable();

        Assert.False(result.Available);
        Assert.Null(result.Value);
    }
}
