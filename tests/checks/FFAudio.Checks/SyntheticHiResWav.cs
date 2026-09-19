using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace FFAudio.Checks;

// Generates 24-bit PCM WAV fixtures with meaningful low bits.
public static class SyntheticHiResWav
{
    private const int HeaderSize = 44;
    private const int Channels = 2;
    private const int BytesPerSample = 3;
    private const int BytesPerFrame = BytesPerSample * Channels;

    // Walk the full 24-bit range so truncation affects nearly every frame.
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
