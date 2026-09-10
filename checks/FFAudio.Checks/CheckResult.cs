using System;

namespace FFAudio.Checks;

// One check's verdict, in a shape a phone can print and a desktop test can
// assert on.
public sealed record CheckResult(string Name, bool Passed, string Detail, TimeSpan Elapsed)
{
    public override string ToString() =>
        $"{(Passed ? "PASS" : "FAIL")}  {Name}  ({Elapsed.TotalMilliseconds:F0}ms)"
        + (Detail.Length == 0 ? "" : $"\n      {Detail}");
}

// Thrown by a check that did not hold. Nothing catches it but the runner.
public sealed class CheckFailedException(string message) : Exception(message);
