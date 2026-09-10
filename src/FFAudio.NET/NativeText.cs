using System;
using System.Runtime.InteropServices;

namespace FFAudio
{
    // Reading a string out of the façade, which never allocates and never
    // reports the size it wanted - only that the buffer it was handed was too
    // small. So every read is a loop, and the loop is here rather than in each
    // of the six callers.
    //
    // Not part of Decoder any more because it stopped being only Decoder's:
    // FFmpegBuild asks the library about itself with no decoder open at all.
    internal static unsafe class NativeText
    {
        internal delegate int FillOne(byte* buffer, int bufferBytes);

        internal delegate int FillTwo(byte* first, int firstBytes, byte* second, int secondBytes);

        // The façade reports a buffer it could not fill but not the size it
        // wanted, so the answer is to ask again with more room. Lyrics and
        // comment tags are the reason this is not a fixed 256 bytes; the cap
        // is there so a corrupt length cannot turn into an allocation loop.
        internal const int MaxTextBytes = 1 << 20;

        internal static string ReadString(FillOne fill)
        {
            for (var capacity = 256; ; capacity *= 4)
            {
                var buffer = new byte[capacity];
                int rc;
                fixed (byte* pointer = buffer)
                    rc = fill(pointer, capacity);

                if (rc == Native.Truncated && capacity < MaxTextBytes)
                    continue;
                if (rc != Native.Ok)
                    throw new DecodeException("Could not read a text field", rc);

                return Text(buffer);
            }
        }

        internal static void ReadPair(FillTwo fill, out string first, out string second)
        {
            for (var capacity = 256; ; capacity *= 4)
            {
                var a = new byte[capacity];
                var b = new byte[capacity];
                int rc;
                fixed (byte* pa = a)
                fixed (byte* pb = b)
                    rc = fill(pa, capacity, pb, capacity);

                if (rc == Native.Truncated && capacity < MaxTextBytes)
                    continue;
                if (rc != Native.Ok)
                    throw new DecodeException("Could not read a text field", rc);

                first = Text(a);
                second = Text(b);
                return;
            }
        }

        // Every string the façade writes is NUL-terminated, so the terminator
        // rather than the buffer length is what says where it ends.
        internal static string Text(byte[] buffer)
        {
            fixed (byte* pointer = buffer)
                return Marshal.PtrToStringUTF8((IntPtr)pointer) ?? "";
        }
    }
}
