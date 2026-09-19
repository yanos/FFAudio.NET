using System;
using System.Collections.Generic;

using Microsoft.Extensions.Logging;

namespace FFAudio.Checks;

// An ILogger that keeps what it was told, so a check can ask what the decoder
// said rather than only what it did.
//
// OpenStream's format-hint fallback is the case: a wrong hint still opens,
// because the stream is rewound and probed, so the samples cannot tell a
// working fallback from a hint that was never tried. The warning is the only
// evidence the forced open happened and failed, and it is also the only way a
// caller ever learns its catalog mislabels a track.
public sealed class RecordingLogger : ILogger
{
    public List<(LogLevel Level, string Message)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                            Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}
