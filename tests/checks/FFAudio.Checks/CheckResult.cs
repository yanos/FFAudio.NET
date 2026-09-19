using System;

namespace FFAudio.Checks;

// A check result shared by device reporters and desktop assertions.
public sealed record CheckResult(string Name, bool Passed, string Detail, TimeSpan Elapsed)
{
    public override string ToString() =>
        $"{(Passed ? "PASS" : "FAIL")}  {Name}  ({Elapsed.TotalMilliseconds:F0}ms)"
        + (Detail.Length == 0 ? "" : $"\n      {Detail}");
}

// Signals a failed check to the runner.
public sealed class CheckFailedException(string message) : Exception(message);
