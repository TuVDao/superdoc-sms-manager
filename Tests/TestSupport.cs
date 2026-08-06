using System.Reflection;
using SuperDoc.Sms.Models;
using Xunit;

// PhoneNumber.DefaultCountryCode is process-wide state, so tests that set it cannot safely run
// beside tests that read it. Parallelism buys nothing here - the whole suite is pure arithmetic
// and file reads - so it is disabled rather than worked around with locks.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SuperDoc.Sms.Tests;

/// <summary>Locates repository files that the tests read directly, such as the language packs.</summary>
internal static class RepoLayout
{
    private static readonly Lazy<string> RootPath = new(FindRoot);

    /// <summary>The repository root, found by walking up from the test assembly.</summary>
    public static string Root => RootPath.Value;

    public static string StringsDirectory => Path.Combine(Root, "WinUI", "Strings");

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (dir.EnumerateFiles("*.sln").Any())
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find the repository root: no .sln above the test assembly.");
    }
}

/// <summary>
/// Sets the phone country for the duration of a test and puts it back afterwards, so one test's
/// country cannot leak into the next.
/// </summary>
internal sealed class CountryScope : IDisposable
{
    private readonly string _previous;

    public CountryScope(string? countryCode)
    {
        _previous = PhoneNumber.DefaultCountryCode;
        PhoneNumber.SetDefaultCountryCode(countryCode);
    }

    public void Dispose() => PhoneNumber.SetDefaultCountryCode(_previous);
}
