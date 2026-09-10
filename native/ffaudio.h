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
#define FFAUDIO_ERR_NOT_PRESENT (FFAUDIO_ERR_BASE - 6)
#define FFAUDIO_ERR_TRUNCATED   (FFAUDIO_ERR_BASE - 7)

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


// ------------------------------------------------------- what the file says
//
// Everything below is metadata rather than audio, and it is here for one
// reason: a caller that cannot ask this façade reaches past it to FFmpeg, and
// then has two routes to FFmpeg to build, ship and keep in step. That is the
// exact outcome the façade exists to prevent, so the cheap questions every
// consumer asks are answered on this side of the header.
//
// None of it allocates. Strings go into caller-owned buffers and are always
// NUL-terminated; a buffer too small yields FFAUDIO_ERR_TRUNCATED and a
// truncated-but-valid string, so a caller can retry larger or accept it.
//
// These are additions, not changes - no existing function or struct moved -
// so FFAUDIO_ABI_VERSION stays 1. A library older than these symbols fails at
// the first call to one with a link error rather than an ABI mismatch, which
// is the one thing a version bump would have bought and is not worth the
// churn while nothing has shipped.

// How many tags this file carries. Container-level first, then the audio
// stream's own - Vorbis comments in an Ogg live on the stream while ID3 on an
// MP3 lives on the container, and a caller asking "what are this file's tags"
// means both. Keys are FFmpeg's normalised names ("title", "artist",
// "album"...) where it has one, and the container's raw key where it does not.
// Duplicates are preserved rather than collapsed: a track really can have two
// ARTIST comments, and which one wins is the caller's policy, not this file's.
FFAUDIO_API int ffaudio_decoder_tag_count(ffaudio_decoder *decoder,
                                        int32_t *out_count);

// The tag at index, 0 <= index < the count above. Either buffer may be NULL
// to skip that half.
FFAUDIO_API int ffaudio_decoder_tag_at(ffaudio_decoder *decoder,
                                     int32_t index,
                                     char *key, int32_t key_bytes,
                                     char *value, int32_t value_bytes);

// The embedded cover art, which FFmpeg models as a video stream carrying a
// single attached picture. Returns FFAUDIO_ERR_NOT_PRESENT when there is
// none.
//
// Call it with buffer NULL to be told the size in out_bytes and nothing else,
// then again with storage that fits; the bytes are the original encoded image
// exactly as the container holds it - JPEG or PNG almost always - and this
// façade neither decodes nor rescales it. A buffer that is too small is
// FFAUDIO_ERR_TRUNCATED with the required size in out_bytes, and nothing is
// written, because half a JPEG is not a smaller JPEG.
FFAUDIO_API int ffaudio_decoder_cover_art(ffaudio_decoder *decoder,
                                        uint8_t *buffer, int32_t buffer_bytes,
                                        int32_t *out_bytes,
                                        char *mime, int32_t mime_bytes);

// The delivered PCM's channel layout in FFmpeg's canonical text form -
// "stereo", "5.1(side)", "mono". The struct stays on this side of the header,
// which is the whole trick: ffaudio_decoder_format.channels is a number, and
// a number cannot say which channel is which.
//
// This describes what read() hands back, after any requested down- or
// up-mix, rather than what the source held.
FFAUDIO_API int ffaudio_decoder_channel_layout(ffaudio_decoder *decoder,
                                             char *buffer, int32_t buffer_bytes);

// The source's codec and container, by FFmpeg's short names - "flac" and
// "flac", "alac" and "mov,mp4,m4a,3gp,3g2,mj2". Either buffer may be NULL.
// One call rather than two because nothing ever wants only one of them.
FFAUDIO_API int ffaudio_decoder_names(ffaudio_decoder *decoder,
                                    char *codec, int32_t codec_bytes,
                                    char *container, int32_t container_bytes);

// Into caller-owned storage; never allocates, always NUL-terminates.
FFAUDIO_API void ffaudio_error_string(int code, char *buffer, int32_t buffer_bytes);

FFAUDIO_API int32_t ffaudio_abi_version(void);

#ifdef __cplusplus
}
#endif

#endif
