using System.Linq;

using FFAudio.Checks;

using Xunit;

namespace FFAudio.Tests;

// Run the device check suite on desktop to validate the checks themselves.
[Trait("Category", "RequiresNative")]
public class DeviceChecksTests
{
    [Fact]
    public void Every_device_check_passes_on_this_desktop()
    {
        var results = DecodeChecks.RunAll();

        Assert.NotEmpty(results);
        Assert.All(results, result => Assert.True(result.Passed, result.ToString()));
    }
}
