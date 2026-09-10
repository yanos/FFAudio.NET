using System.Linq;

using FFAudio.Checks;

using Xunit;

namespace FFAudio.Tests;

// The phone checks, run here.
//
// They exist to run on iOS and Android, where there is no xUnit host - but a
// check that is only ever exercised on a device is a check nobody can trust,
// because a failure on a phone would be ambiguous between the platform and
// the check itself. Running them on every desktop first means a red simulator
// run says something about the simulator.
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
