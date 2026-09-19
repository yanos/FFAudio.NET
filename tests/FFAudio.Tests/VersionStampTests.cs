using System;
using System.Reflection;
using System.Text.RegularExpressions;

using Xunit;

namespace FFAudio.Tests;

// The version MinVer stamps into FFAudio.NET.dll, from git tags.
//
// Every way this goes wrong still builds and passes every other test: MinVer
// missing gives the SDK's 1.0.0, a shallow clone gives MinVer's 0.0.0-alpha.0
// fallback, and neither says a word. The informational version is also the
// only thing in a shipped DLL that says which commit it came from.
public class VersionStampTests
{
    private static readonly Assembly Library = typeof(Decoder).Assembly;

    // major.minor.patch, an optional pre-release, then +<40-hex commit>.
    private static readonly Regex Informational = new(
        @"^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:-[0-9A-Za-z.-]+)?\+(?<commit>[0-9a-f]{40})$");

    private static Match Stamp()
    {
        var version = Library.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Assert.NotNull(version);

        var match = Informational.Match(version);
        Assert.True(match.Success, $"\"{version}\" is not a version plus a commit hash.");
        return match;
    }

    [Fact]
    public void The_version_comes_from_minver()
    {
        var match = Stamp();
        var version = match.Value[..match.Value.IndexOf('+')];

        Assert.NotEqual("1.0.0", version);
        Assert.False(
            version.StartsWith("0.0.0-alpha.0", StringComparison.Ordinal),
            $"\"{version}\" is MinVer's no-tags fallback: the checkout has no history. " +
            "In CI, actions/checkout needs fetch-depth: 0.");
    }

    // A test assembly built in the same build gets its version the same way,
    // so the two can only differ if the library was not rebuilt.
    [Fact]
    public void The_version_matches_this_build()
    {
        var tests = typeof(VersionStampTests).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        Assert.Equal(tests, Stamp().Value);
    }

    [Fact]
    public void The_commit_is_the_one_ci_built()
    {
        var sha = Environment.GetEnvironmentVariable("GITHUB_SHA");
        if (string.IsNullOrEmpty(sha))
            Assert.Skip("GITHUB_SHA is not set; not running in GitHub Actions.");

        Assert.Equal(sha, Stamp().Groups["commit"].Value);
    }

    // The numeric versions cannot carry a pre-release, so MinVer derives them:
    // the file version is major.minor.patch.0, and the assembly version stops
    // at the major so that binding redirects are only needed across one.
    [Fact]
    public void The_numeric_versions_follow_the_informational_one()
    {
        var match = Stamp();
        var major = int.Parse(match.Groups["major"].Value);
        var minor = int.Parse(match.Groups["minor"].Value);
        var patch = int.Parse(match.Groups["patch"].Value);

        Assert.Equal(new Version(major, 0, 0, 0), Library.GetName().Version);
        Assert.Equal(
            $"{major}.{minor}.{patch}.0",
            Library.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version);
    }
}
