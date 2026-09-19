using System;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

using Xunit;

namespace FFAudio.Tests;

// Verify MinVer's tag-derived assembly metadata and commit stamp.
public class VersionStampTests
{
    private static readonly Assembly Library = typeof(Decoder).Assembly;

    // major.minor.patch[-prerelease]+<40-hex commit>
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

        // Absent when MinVer did not run and the SDK's 1.0.0 default was stamped instead.
        var minver = Library.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(a => a.Key == "MinVerVersion")?.Value;
        Assert.False(string.IsNullOrEmpty(minver), "MinVer did not stamp the library.");
        Assert.Equal(minver, version);

        Assert.False(
            version.StartsWith("0.0.0-alpha.0", StringComparison.Ordinal),
            $"\"{version}\" is MinVer's no-tags fallback: the checkout has no history. " +
            "In CI, actions/checkout needs fetch-depth: 0.");
    }

    // The test and library assemblies should share one build version.
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

    // Numeric versions omit prerelease data; assembly compatibility follows major.
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
