using Monitor.Core.Errors;

namespace Monitor.UnitTests.Errors;

public class AppExceptionTests
{
    [Fact]
    public void Constructor_DefaultIsOperational_IsTrue()
    {
        var ex = new AppException(400, "Bad request");

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("Bad request", ex.Message);
        Assert.True(ex.IsOperational);
    }

    [Fact]
    public void Constructor_ExplicitIsOperationalFalse_IsRespected()
    {
        var ex = new AppException(500, "boom", isOperational: false);

        Assert.Equal(500, ex.StatusCode);
        Assert.Equal("boom", ex.Message);
        Assert.False(ex.IsOperational);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(422)]
    [InlineData(500)]
    public void Constructor_StoresArbitraryStatusCode(int statusCode)
    {
        var ex = new AppException(statusCode, "message");

        Assert.Equal(statusCode, ex.StatusCode);
    }

    [Fact]
    public void AppException_IsAnException_AndCarriesMessageThroughBaseClass()
    {
        Exception ex = new AppException(401, "Invalid email or password");

        Assert.Equal("Invalid email or password", ex.Message);
        Assert.IsAssignableFrom<Exception>(ex);
    }

    [Fact]
    public void Constructor_EmptyMessage_IsPreservedVerbatim()
    {
        var ex = new AppException(400, string.Empty);

        Assert.Equal(string.Empty, ex.Message);
    }
}
