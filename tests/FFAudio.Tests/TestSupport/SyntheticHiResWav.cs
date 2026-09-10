using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace FFAudio.Tests;

// 24-bit PCM WAV at an arbitrary sample rate.
//
// The point of a fixture with meaningful bits below bit 8 is that it is an
// oracle for the claim this decoder is built on: that a 24-bit source survives
// to the caller intact. Every decoder that narrows to 16 bits somewhere - and
// that is most of the ones a phone offers - returns these files with those bits
// gone, and the loss is invisible unless the fixture puts something there to
// lose.
public static class SyntheticHiResWav
{
    private const int HeaderSize = 44;
    private const int Channels = 2;
    private const int BytesPerSample = 3;
    private const int BytesPerFrame = BytesPerSample * Channels;

    // A counter that walks the whole 24-bit range, so consecutive frames
    // differ in the low byte as well as the high ones. Truncating this to 16
    // bits is detectable at almost every frame rather than at a few.
    public static Func<int, int> Ramp24() => frame => unchecked((frame * 7919) & 0xFFFFFF) - 0x800000;

    public static string CreateFile(string directory, string fileName, int sampleRate, int frameCount, Func<int, int> sampleAt)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, Build(sampleRate, frameCount, sampleAt));
        return path;
    }

    public static byte[] Build(int sampleRate, int frameCount, Func<int, int> sampleAt)
    {
        var dataSize = frameCount * BytesPerFrame;
        var buffer = new byte[HeaderSize + dataSize];
        var span = buffer.AsSpan();

        Encoding.ASCII.GetBytes("RIFF").CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + dataSize);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(span[8..]);

        Encoding.ASCII.GetBytes("fmt ").CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], Channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], sampleRate * BytesPerFrame);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], BytesPerFrame);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], BytesPerSample * 8);

        Encoding.ASCII.GetBytes("data").CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataSize);

        var data = span[HeaderSize..];
        for (var frame = 0; frame < frameCount; frame++)
        {
            var sample = sampleAt(frame);
            for (var channel = 0; channel < Channels; channel++)
                WriteInt24(data.Slice(frame * BytesPerFrame + channel * BytesPerSample), sample);
        }

        return buffer;
    }

    public static void WriteInt24(Span<byte> destination, int value)
    {
        destination[0] = (byte)value;
        destination[1] = (byte)(value >> 8);
        destination[2] = (byte)(value >> 16);
    }

    public static int ReadInt24(ReadOnlySpan<byte> source) =>
        (source[0] | (source[1] << 8) | (source[2] << 16)) << 8 >> 8;
}
