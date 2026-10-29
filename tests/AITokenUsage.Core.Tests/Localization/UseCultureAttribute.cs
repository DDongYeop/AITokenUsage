using System.Globalization;
using System.Reflection;
using Xunit.Sdk;

namespace AITokenUsage.Core.Tests.Localization;

/// <summary>Runs one test (or every test in a class) under the given culture and UI culture.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class UseCultureAttribute(string name) : BeforeAfterTestAttribute
{
    private CultureInfo? _culture;
    private CultureInfo? _uiCulture;

    public override void Before(MethodInfo methodUnderTest)
    {
        _culture = CultureInfo.CurrentCulture;
        _uiCulture = CultureInfo.CurrentUICulture;
        var target = CultureInfo.GetCultureInfo(name);
        CultureInfo.CurrentCulture = target;
        CultureInfo.CurrentUICulture = target;
    }

    public override void After(MethodInfo methodUnderTest)
    {
        CultureInfo.CurrentCulture = _culture!;
        CultureInfo.CurrentUICulture = _uiCulture!;
    }
}
