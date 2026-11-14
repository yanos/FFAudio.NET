using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using FFAudio.Checks;

using Xunit;

namespace FFAudio.Tests;

[Trait("Category", "RequiresNative")]
public class DecoderConcurrencyTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static byte[] Audio() =>
        SyntheticHiResWav.Build(96000, 960000, SyntheticHiResWav.Ramp24());

    [Theory]
    [InlineData("read")]
    [InlineData("seek")]
    [InlineData("tags")]
    [InlineData("cover")]
    [InlineData("dispose")]
    public async Task Operations_wait_for_an_active_native_read(string operation)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var bytes = Audio();
        using var source = new PausingStream(bytes);
        using var decoder = Decoder.OpenStream(source, SampleFormat.S24, ownsStream: true);
        source.PauseNextRead();

        // One native call remains on the stack while its managed IO callback
        // waits. This places the second operation inside the former race.
        var read = Task.Run(() => decoder.Read(new byte[bytes.Length]), cancellationToken);
        Task? other = null;
        try
        {
            await source.ReadEntered.Task.WaitAsync(Timeout, cancellationToken);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            other = Task.Run(() =>
            {
                started.SetResult();
                if (operation == "dispose")
                    Parallel.For(0, 16, _ => decoder.Dispose());
                else
                    Operate(decoder, operation);
            }, cancellationToken);
            await started.Task.WaitAsync(Timeout, cancellationToken);

            Assert.NotSame(other, await Task.WhenAny(other, Task.Delay(100, cancellationToken)));
            Assert.Equal(0, source.DisposeCount);
        }
        finally
        {
            source.ContinueRead.Set();
            // Always release and join native work, even when the test cancels.
            Assert.True(await read.WaitAsync(Timeout, CancellationToken.None) > 0);
            if (other is not null)
                await other.WaitAsync(Timeout, CancellationToken.None);
        }

        if (operation == "dispose")
        {
            Assert.Equal(1, source.DisposeCount);
            Assert.False(source.CanRead);
            Assert.Throws<ObjectDisposedException>(() => decoder.Read(new byte[8]));
            Assert.Throws<ObjectDisposedException>(() => decoder.Seek(TimeSpan.Zero));
            Assert.Throws<ObjectDisposedException>(() => decoder.TryReadCoverArt());
            Assert.Throws<ObjectDisposedException>(() => _ = decoder.Tags);

            // Competing/repeated disposals must not release callback state or
            // an owned stream more than once.
            Parallel.For(0, 16, _ => decoder.Dispose());
            Assert.Equal(1, source.DisposeCount);
        }
        else
        {
            decoder.Seek(TimeSpan.Zero);
            Assert.True(decoder.Read(new byte[64]) > 0);
        }
    }

    [Theory]
    [InlineData("read")]
    [InlineData("seek")]
    [InlineData("tags")]
    [InlineData("cover")]
    [InlineData("dispose")]
    public void Stream_callbacks_cannot_reenter_native_operations(string operation)
    {
        var bytes = Audio();
        using var source = new CallbackStream(bytes);
        using var decoder = Decoder.OpenStream(source, SampleFormat.S24, ownsStream: true);
        Exception? rejected = null;
        source.BeforeRead = () => rejected = Record.Exception(() => Operate(decoder, operation));

        Assert.True(decoder.Read(new byte[bytes.Length]) > 0);

        Assert.IsType<InvalidOperationException>(rejected);
        Assert.True(source.CanRead);
        decoder.Seek(TimeSpan.Zero);
        Assert.True(decoder.Read(new byte[64]) > 0);
    }

    private static void Operate(Decoder decoder, string operation)
    {
        switch (operation)
        {
            case "read": decoder.Read(new byte[64]); break;
            case "seek": decoder.Seek(TimeSpan.Zero); break;
            case "tags": _ = decoder.Tags; break;
            case "cover": decoder.TryReadCoverArt(); break;
            case "dispose": decoder.Dispose(); break;
            default: throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private sealed class PausingStream(byte[] bytes) : MemoryStream(bytes)
    {
        private int _pause;
        private int _disposeCount;
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public TaskCompletionSource ReadEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim ContinueRead { get; } = new();

        public void PauseNextRead() => Volatile.Write(ref _pause, 1);

        public override int Read(Span<byte> buffer)
        {
            if (Interlocked.Exchange(ref _pause, 0) != 0)
            {
                ReadEntered.SetResult();
                if (!ContinueRead.Wait(Timeout))
                    throw new TimeoutException("The test did not release the native read callback.");
            }
            return base.Read(buffer);
        }

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Increment(ref _disposeCount) == 1 && disposing)
                ContinueRead.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class CallbackStream(byte[] bytes) : MemoryStream(bytes)
    {
        public Action? BeforeRead;

        public override int Read(Span<byte> buffer)
        {
            Interlocked.Exchange(ref BeforeRead, null)?.Invoke();
            return base.Read(buffer);
        }
    }
}
