// ffaudio - a narrow, audio-only façade over FFmpeg's decode libraries.
//
// The point of this file is that it is small. A player does not want FFmpeg's
// API; it wants one sentence of it - "open this, tell me its PCM format, give
// me interleaved samples, seek, close" - and it wants that sentence to have a
// stable ABI across every platform head and every AOT runtime it ships on.
// Every AVFrame, AVPacket, AVChannelLayout and ownership rule stays on the C
// side of this header, so a binding over it is plain P/Invoke (or FFI) over
// ints and byte buffers, and nothing in that binding has to track an FFmpeg
// struct layout.
//
// That is also why this is not FFmpeg.AutoGen: generated bindings would move
// the whole of FFmpeg's ABI into the caller's language, and the caller would
// still have to build and ship the libraries.
//
// Links only against LGPL FFmpeg: avformat, avcodec, avutil, swresample. No
// GPL component may be enabled in the FFmpeg this is built against.

#ifndef FFAUDIO_H
#define FFAUDIO_H

#include <stdint.h>

#if defined(_WIN32)
#  define FFAUDIO_API __declspec(dllexport)
#else
#  define FFAUDIO_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

// Bumped whenever anything below changes shape. The managed side checks it at
// load and refuses a library it was not built against, because the failure
// mode of a silent mismatch is a struct read at the wrong offsets.
#define FFAUDIO_ABI_VERSION 1

// What the caller wants out. FFAUDIO_SAMPLE_S24 is packed 3-byte little-endian,
// which is what miniaudio's ma_format_s24 expects and is not a format
// swresample can produce - the façade packs it from S32 (see
// ffaudio_decoder_read). The others are swresample's own.
typedef enum {
    FFAUDIO_SAMPLE_S16 = 0,
    FFAUDIO_SAMPLE_S24 = 1,
    FFAUDIO_SAMPLE_S32 = 2,
    FFAUDIO_SAMPLE_F32 = 3
} ffaudio_sample_format;

// 0 is success. Negative values below FFAUDIO_ERR_BASE are this façade's own;
// anything else negative is an AVERROR passed through untouched, so that
// ffaudio_error_string can hand back FFmpeg's own diagnosis rather than
// flattening every failure into "could not open".
#define FFAUDIO_OK              0
#define FFAUDIO_EOF             1
#define FFAUDIO_ERR_BASE        (-10000)
#define FFAUDIO_ERR_ARGUMENT    (FFAUDIO_ERR_BASE - 1)
#define FFAUDIO_ERR_NO_AUDIO    (FFAUDIO_ERR_BASE - 2)
#define FFAUDIO_ERR_NO_MEMORY   (FFAUDIO_ERR_BASE - 3)
#define FFAUDIO_ERR_ABI         (FFAUDIO_ERR_BASE - 4)
#define FFAUDIO_ERR_IO          (FFAUDIO_ERR_BASE - 5)

// Read at most buf_size bytes. Returns the count, 0 at end of stream, or a
// negative value for an error. This is FFmpeg's own AVIOContext read
// signature on purpose: on the managed side it is SeekableHttpStream.Read
// with the arguments rearranged, which is what makes the streaming work built
// for LibVLC carry over unchanged.
typedef int  (*ffaudio_read_fn)(void *opaque, uint8_t *buffer, int buf_size);
// whence is SEEK_SET/SEEK_CUR/SEEK_END, or FFAUDIO_SEEK_SIZE to be asked for
// the total length without moving. Returns the new position, or negative.
typedef int64_t (*ffaudio_seek_fn)(void *opaque, int64_t offset, int whence);

#define FFAUDIO_SEEK_SIZE 0x10000

typedef struct {
    int32_t sample_rate;      // of the delivered PCM, after any resample
    int32_t channels;         // of the delivered PCM
    int32_t sample_format;    // ffaudio_sample_format actually being delivered
    int32_t source_bit_depth; // meaningful bits in the source: 16, 24, 32...
    int32_t source_sample_rate;
    int32_t source_channels;
    int64_t duration_ms;      // -1 when the container does not say
} ffaudio_decoder_format;

typedef struct ffaudio_decoder ffaudio_decoder;

// requested_sample_rate/channels of 0 mean "whatever the source is", which is
// how a bit-perfect direct-mode open asks for no conversion at all.
FFAUDIO_API int ffaudio_decoder_open_path(const char *path,
                                        int32_t requested_format,
                                        int32_t requested_sample_rate,
                                        int32_t requested_channels,
                                        ffaudio_decoder **out_decoder);

// size may be -1 when unknown. seekable 0 makes this a forward-only stream,
// and ffaudio_decoder_seek will then refuse. format_hint may be NULL; when set
// it names a demuxer to force (FFmpeg's short name, e.g. "mp4"), skipping
// probing on a stream whose container is already known from the catalog.
FFAUDIO_API int ffaudio_decoder_open_io(void *opaque,
                                      ffaudio_read_fn read,
                                      ffaudio_seek_fn seek,
                                      int64_t size,
                                      int32_t seekable,
                                      const char *format_hint,
                                      int32_t requested_format,
                                      int32_t requested_sample_rate,
                                      int32_t requested_channels,
                                      ffaudio_decoder **out_decoder);

FFAUDIO_API int ffaudio_decoder_get_format(ffaudio_decoder *decoder,
                                         ffaudio_decoder_format *out_format);

// Fills up to buffer_bytes of interleaved PCM. Writes the byte count to
// out_bytes, which is short only at end of stream. Returns FFAUDIO_OK,
// FFAUDIO_EOF once nothing more will come, or a negative error.
FFAUDIO_API int ffaudio_decoder_read(ffaudio_decoder *decoder,
                                   uint8_t *buffer,
                                   int32_t buffer_bytes,
                                   int32_t *out_bytes);

// Lands on or before position_ms - the demuxer is keyframe-bound, so the
// caller has to be told where it actually landed rather than assuming.
// Writes that to out_landed_ms.
FFAUDIO_API int ffaudio_decoder_seek(ffaudio_decoder *decoder,
                                   int64_t position_ms,
                                   int64_t *out_landed_ms);

FFAUDIO_API void ffaudio_decoder_close(ffaudio_decoder *decoder);

// Into caller-owned storage; never allocates, always NUL-terminates.
FFAUDIO_API void ffaudio_error_string(int code, char *buffer, int32_t buffer_bytes);

FFAUDIO_API int32_t ffaudio_abi_version(void);

#ifdef __cplusplus
}
#endif

#endif
