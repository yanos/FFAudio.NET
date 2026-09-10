using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FFAudio.Tests;

// A small AIFF carrying an ID3v2 tag, which is to say: a real file with real
// metadata, built without an encoder.
//
// Phase 3 of the plan adds tags, cover art, channel layout and codec names,
// and every one of those needs a fixture that actually has them - a WAV of
// synthetic PCM proves none of it. The obvious way to get one is to shell out
// to the ffmpeg binary, and that is exactly what this file exists to avoid:
// the Linux CI job installs libavformat-dev and no binary at all, so a fixture
// generated that way would quietly skip on the platform most likely to differ.
//
// AIFF is the container that makes it possible. Its audio is raw big-endian
// PCM, so the samples are just written; and libavformat's aiffdec reads an
// "ID3 " chunk with the same parser it uses on an MP3, so a hand-built ID3v2.3
// tag gets flattened into the same metadata dictionary and the same
// ATTACHED_PIC stream that a tagged MP3 would produce. What is under test is
// the façade's reading of those, not FFmpeg's parsing of them, so a fixture
// that reaches the same structures by a simpler road is worth more than a
// realistic one nobody can build everywhere.
public static class SyntheticTaggedAiff
{
    public const string Title = "A quiet ramp";
    public const string Artist = "The Fixture";
    public const string Album = "Synthetic Hi-Res";

    // A 1x1 red PNG. Small because the assertion is that the bytes come back
    // exactly, and a byte-for-byte comparison does not get truer with size.
    public static byte[] CoverPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC");

    public static string CreateFile(string directory, string fileName, int sampleRate, int frameCount, int channels = 2)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, Build(sampleRate, frameCount, channels));
        return path;
    }

    public static byte[] Build(int sampleRate, int frameCount, int channels = 2)
    {
        var audio = new byte[frameCount * channels * 2];
        for (var frame = 0; frame < frameCount; frame++)
        {
            for (var channel = 0; channel < channels; channel++)
            {
                // Big-endian, because that is what AIFF means by PCM.
                var sample = (short)((frame * 601 + channel * 7919) & 0x7FFF);
                var at = (frame * channels + channel) * 2;
                BinaryPrimitives.WriteInt16BigEndian(audio.AsSpan(at), sample);
            }
        }

        var comm = new MemoryStream();
        WriteBigEndian(comm, (short)channels);
        WriteBigEndian(comm, frameCount);
        WriteBigEndian(comm, (short)16);
        comm.Write(Extended80(sampleRate));

        var ssnd = new MemoryStream();
        WriteBigEndian(ssnd, 0); // offset
        WriteBigEndian(ssnd, 0); // block size
        ssnd.Write(audio);

        var body = new MemoryStream();
        body.Write(Encoding.ASCII.GetBytes("AIFF"));
        WriteChunk(body, "COMM", comm.ToArray());
        WriteChunk(body, "SSND", ssnd.ToArray());
        WriteChunk(body, "ID3 ", Id3v2());

        var file = new MemoryStream();
        file.Write(Encoding.ASCII.GetBytes("FORM"));
        WriteBigEndian(file, (int)body.Length);
        body.Position = 0;
        body.CopyTo(file);
        return file.ToArray();
    }

    // IFF chunks are word-aligned: an odd-length chunk is followed by a pad
    // byte that its own length does not count. Getting this wrong shifts
    // every chunk after it, and the demuxer reports a corrupt file rather
    // than a misaligned one.
    private static void WriteChunk(Stream stream, string id, byte[] content)
    {
        stream.Write(Encoding.ASCII.GetBytes(id));
        WriteBigEndian(stream, content.Length);
        stream.Write(content);
        if (content.Length % 2 != 0)
            stream.WriteByte(0);
    }

    private static byte[] Id3v2()
    {
        var frames = new MemoryStream();
        WriteTextFrame(frames, "TIT2", Title);
        WriteTextFrame(frames, "TPE1", Artist);
        WriteTextFrame(frames, "TALB", Album);
        WritePictureFrame(frames, "image/png", CoverPng());

        var tag = new MemoryStream();
        tag.Write(Encoding.ASCII.GetBytes("ID3"));
        tag.WriteByte(3); // v2.3
        tag.WriteByte(0);
        tag.WriteByte(0); // no flags
        tag.Write(Syncsafe((int)frames.Length));
        frames.Position = 0;
        frames.CopyTo(tag);
        return tag.ToArray();
    }

    private static void WriteTextFrame(Stream stream, string id, string text)
    {
        var content = new MemoryStream();
        content.WriteByte(0); // ISO-8859-1
        content.Write(Encoding.Latin1.GetBytes(text));
        WriteFrame(stream, id, content.ToArray());
    }

    private static void WritePictureFrame(Stream stream, string mime, byte[] image)
    {
        var content = new MemoryStream();
        content.WriteByte(0); // ISO-8859-1
        content.Write(Encoding.ASCII.GetBytes(mime));
        content.WriteByte(0);
        content.WriteByte(3); // front cover
        content.WriteByte(0); // empty description
        content.Write(image);
        WriteFrame(stream, "APIC", content.ToArray());
    }

    // A v2.3 frame size is a plain big-endian int. Only the tag header's own
    // size is syncsafe, which is the detail that makes hand-built ID3 tags
    // fail to parse.
    private static void WriteFrame(Stream stream, string id, byte[] content)
    {
        stream.Write(Encoding.ASCII.GetBytes(id));
        WriteBigEndian(stream, content.Length);
        stream.WriteByte(0);
        stream.WriteByte(0);
        stream.Write(content);
    }

    private static byte[] Syncsafe(int value) =>
    [
        (byte)((value >> 21) & 0x7F),
        (byte)((value >> 14) & 0x7F),
        (byte)((value >> 7) & 0x7F),
        (byte)(value & 0x7F),
    ];

    // AIFF stores its sample rate as an 80-bit IEEE extended float, which is
    // the one part of the format that is not obvious. The integer bit is
    // explicit here, unlike in the 32- and 64-bit forms.
    private static byte[] Extended80(int value)
    {
        var bytes = new byte[10];
        if (value == 0)
            return bytes;

        var exponent = 16383 + 63;
        var mantissa = (ulong)value;
        while ((mantissa & 0x8000000000000000UL) == 0)
        {
            mantissa <<= 1;
            exponent--;
        }

        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(0), (ushort)exponent);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(2), mantissa);
        return bytes;
    }

    private static void WriteBigEndian(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteBigEndian(Stream stream, short value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteInt16BigEndian(bytes, value);
        stream.Write(bytes);
    }
}
