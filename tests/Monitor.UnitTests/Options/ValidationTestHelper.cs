using System.ComponentModel.DataAnnotations;

namespace Monitor.UnitTests.Options;

/// <summary>
/// Runs the same validation ASP.NET Core's ValidateDataAnnotations() would run
/// (including IValidatableObject.Validate when implemented), without needing a
/// full DI container for every single-class test.
/// </summary>
internal static class ValidationTestHelper
{
    public static IList<ValidationResult> Validate(object instance)
    {
        var context = new ValidationContext(instance);
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(instance, context, results, validateAllProperties: true);
        return results;
    }

    public static bool IsValid(object instance) => Validate(instance).Count == 0;
}
