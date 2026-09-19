// A stable, audio-only ABI over FFmpeg's avformat, avcodec, avutil, and
// swresample libraries. FFmpeg types and ownership remain private to C.

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

// Increment when an exported signature or struct layout changes incompatibly.
#define FFAUDIO_ABI_VERSION 1

// S24 is packed three-byte little-endian; the other formats map to swresample.
typedef enum {
    FFAUDIO_SAMPLE_S16 = 0,
    FFAUDIO_SAMPLE_S24 = 1,
    FFAUDIO_SAMPLE_S32 = 2,
    FFAUDIO_SAMPLE_F32 = 3
} ffaudio_sample_format;

// Other negative values are FFmpeg AVERROR codes passed through unchanged.
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

// Returns bytes read, zero at end of stream, or a negative value on error.
typedef int  (*ffaudio_read_fn)(void *opaque, uint8_t *buffer, int buf_size);
// whence is SEEK_SET, SEEK_CUR, SEEK_END, or FFAUDIO_SEEK_SIZE. Returns the
// resulting position or a negative value on error.
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

// A requested sample rate or channel count of zero preserves the source value.
FFAUDIO_API int ffaudio_decoder_open_path(const char *path,
                                        int32_t requested_format,
                                        int32_t requested_sample_rate,
                                        int32_t requested_channels,
                                        ffaudio_decoder **out_decoder);

// size may be -1. A false seekable value creates a forward-only stream.
// format_hint optionally forces an FFmpeg demuxer by short name.
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

// Writes up to buffer_bytes of interleaved PCM and reports the byte count.
FFAUDIO_API int ffaudio_decoder_read(ffaudio_decoder *decoder,
                                   uint8_t *buffer,
                                   int32_t buffer_bytes,
                                   int32_t *out_bytes);

// Seeks at or before position_ms and reports the actual landing position.
FFAUDIO_API int ffaudio_decoder_seek(ffaudio_decoder *decoder,
                                   int64_t position_ms,
                                   int64_t *out_landed_ms);

FFAUDIO_API void ffaudio_decoder_close(ffaudio_decoder *decoder);

// Metadata strings use caller-owned buffers and are always NUL-terminated.
// Insufficient buffers return FFAUDIO_ERR_TRUNCATED.

// Counts container tags followed by audio-stream tags. Duplicates are preserved.
FFAUDIO_API int ffaudio_decoder_tag_count(ffaudio_decoder *decoder,
                                        int32_t *out_count);

// Reads a tag by index. Either output buffer may be NULL.
FFAUDIO_API int ffaudio_decoder_tag_at(ffaudio_decoder *decoder,
                                     int32_t index,
                                     char *key, int32_t key_bytes,
                                     char *value, int32_t value_bytes);

// Returns unchanged encoded cover art. Pass NULL for buffer to query its size.
// Unsupported image headers report 0 x 0 dimensions. Optional outputs may be NULL.
FFAUDIO_API int ffaudio_decoder_cover_art(ffaudio_decoder *decoder,
                                        uint8_t *buffer, int32_t buffer_bytes,
                                        int32_t *out_bytes,
                                        char *mime, int32_t mime_bytes,
                                        int32_t *out_width, int32_t *out_height);

// Describes the delivered PCM layout in FFmpeg's canonical text form.
FFAUDIO_API int ffaudio_decoder_channel_layout(ffaudio_decoder *decoder,
                                             char *buffer, int32_t buffer_bytes);

// Returns FFmpeg's short codec and container names. Either buffer may be NULL.
FFAUDIO_API int ffaudio_decoder_names(ffaudio_decoder *decoder,
                                    char *codec, int32_t codec_bytes,
                                    char *container, int32_t container_bytes);

// Writes a NUL-terminated error description into caller-owned storage.
FFAUDIO_API void ffaudio_error_string(int code, char *buffer, int32_t buffer_bytes);

FFAUDIO_API int32_t ffaudio_abi_version(void);

// Metadata reported by the loaded FFmpeg binary. All functions write
// NUL-terminated text into caller-owned storage.
FFAUDIO_API int ffaudio_ffmpeg_license(char *buffer, int32_t buffer_bytes);
FFAUDIO_API int ffaudio_ffmpeg_configuration(char *buffer, int32_t buffer_bytes);
FFAUDIO_API int ffaudio_ffmpeg_version(char *buffer, int32_t buffer_bytes);

#ifdef __cplusplus
}
#endif

#endif
