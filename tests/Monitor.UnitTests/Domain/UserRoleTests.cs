using Monitor.Core.Domain;

namespace Monitor.UnitTests.Domain;

public class UserRoleTests
{
    [Theory]
    [InlineData("Admin", UserRole.Admin)]
    [InlineData("Operator", UserRole.Operator)]
    [InlineData("Viewer", UserRole.Viewer)]
    public void TryParse_ValidWireString_ReturnsTrueAndCorrectRole(string wire, UserRole expected)
    {
        var ok = UserRoleExtensions.TryParse(wire, out var role);

        Assert.True(ok);
        Assert.Equal(expected, role);
    }

    [Theory]
    [InlineData("admin")]      // wrong case
    [InlineData("ADMIN")]
    [InlineData("SuperAdmin")]
    [InlineData("")]
    [InlineData(" Admin")]
    public void TryParse_InvalidOrWrongCaseString_ReturnsFalse(string? wire)
    {
        var ok = UserRoleExtensions.TryParse(wire, out var role);

        Assert.False(ok);
        Assert.Equal(default(UserRole), role);
    }

    [Fact]
    public void TryParse_Null_ReturnsFalse()
    {
        var ok = UserRoleExtensions.TryParse(null, out var role);

        Assert.False(ok);
        Assert.Equal(default(UserRole), role);
    }

    [Theory]
    [InlineData(UserRole.Admin, "Admin")]
    [InlineData(UserRole.Operator, "Operator")]
    [InlineData(UserRole.Viewer, "Viewer")]
    public void ToWireString_KnownRole_RendersExactWireValue(UserRole role, string expected)
    {
        Assert.Equal(expected, role.ToWireString());
    }

    [Fact]
    public void ToWireString_UnknownEnumValue_ThrowsArgumentOutOfRangeException()
    {
        var bogus = (UserRole)999;

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => bogus.ToWireString());
        Assert.Equal("role", ex.ParamName);
    }

    [Fact]
    public void RoundTrip_ParseThenRender_IsStable()
    {
        foreach (var wire in new[] { "Admin", "Operator", "Viewer" })
        {
            Assert.True(UserRoleExtensions.TryParse(wire, out var role));
            Assert.Equal(wire, role.ToWireString());
        }
    }
}
