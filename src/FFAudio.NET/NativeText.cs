using System;
using System.Runtime.InteropServices;

namespace FFAudio
{
    // Shared grow-and-retry handling for native UTF-8 output.
    internal static unsafe class NativeText
    {
        internal delegate int FillOne(byte* buffer, int bufferBytes);

        internal delegate int FillTwo(byte* first, int firstBytes, byte* second, int secondBytes);

        // Cap retries so malformed native output cannot grow indefinitely.
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

        // Native strings are NUL-terminated within the supplied buffer.
        internal static string Text(byte[] buffer)
        {
            fixed (byte* pointer = buffer)
                return Marshal.PtrToStringUTF8((IntPtr)pointer) ?? "";
        }
    }
}
