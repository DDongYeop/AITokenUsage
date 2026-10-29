using System.Globalization;
using System.Runtime.CompilerServices;

namespace AITokenUsage.Core.Tests.Localization;

/// <summary>Existing tests assert Korean text: every test thread defaults to ko-KR unless [UseCulture] overrides it.</summary>
internal static class TestCultureSetup
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var korean = CultureInfo.GetCultureInfo("ko-KR");
        CultureInfo.DefaultThreadCurrentCulture = korean;
        CultureInfo.DefaultThreadCurrentUICulture = korean;
    }
}
