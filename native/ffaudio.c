#include "ffaudio.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <libavformat/avformat.h>
#include <libavcodec/avcodec.h>
#include <libavutil/opt.h>
#include <libavutil/channel_layout.h>
#include <libavutil/samplefmt.h>
#include <libswresample/swresample.h>

// Match FFmpeg's default custom AVIO buffer size.
#define FFAUDIO_IO_BUFFER_BYTES 32768

struct ffaudio_decoder {
    AVFormatContext *fmt;
    AVIOContext     *avio;
    AVCodecContext  *codec;
    SwrContext      *swr;
    AVPacket        *packet;
    AVFrame         *frame;
    int              stream_index;

    ffaudio_decoder_format format;

    // Delivered layout after any requested mix.
    AVChannelLayout out_layout;

    // S24 is produced as S32 by swresample, then packed on output.
    enum AVSampleFormat swr_format;
    int swr_bytes_per_frame;
    int out_bytes_per_frame;

    uint8_t *scratch;          // swr_format, swr's destination
    int      scratch_frames;
    uint8_t *pending;          // delivered format, waiting for a read
    int      pending_capacity;
    int      pending_bytes;
    int      pending_offset;

    int      packet_drained;   // no more packets to send
    int      finished;         // the codec has been fully flushed
    int      deferred_error;   // held back while decoded bytes were returned

    void           *io_opaque;
    ffaudio_read_fn  io_read;
    ffaudio_seek_fn  io_seek;
    int             seekable;

    int64_t last_frame_ms;

    // Where the last packet of the audio stream ended, or -1 if unknown.
    int64_t last_packet_end;

    int32_t requested_format;
    int32_t requested_rate;
    int32_t requested_channels;
};

// Translate .NET's zero-byte EOF into FFmpeg's explicit EOF code.
static int io_read_packet(void *opaque, uint8_t *buffer, int buf_size)
{
    ffaudio_decoder *dec = (ffaudio_decoder *)opaque;
    int read = dec->io_read(dec->io_opaque, buffer, buf_size);
    if (read == 0)
        return AVERROR_EOF;
    if (read < 0)
        return AVERROR(EIO);
    return read;
}

static int64_t io_seek(void *opaque, int64_t offset, int whence)
{
    ffaudio_decoder *dec = (ffaudio_decoder *)opaque;
    // AVSEEK_FORCE does not change the underlying seek operation.
    int base = whence & ~AVSEEK_FORCE;
    if (base == AVSEEK_SIZE)
        base = FFAUDIO_SEEK_SIZE;
    return dec->io_seek(dec->io_opaque, offset, base);
}

static enum AVSampleFormat swr_format_for(int32_t requested)
{
    switch (requested) {
        case FFAUDIO_SAMPLE_S16: return AV_SAMPLE_FMT_S16;
        case FFAUDIO_SAMPLE_S24: return AV_SAMPLE_FMT_S32;
        case FFAUDIO_SAMPLE_S32: return AV_SAMPLE_FMT_S32;
        case FFAUDIO_SAMPLE_F32: return AV_SAMPLE_FMT_FLT;
        default:                return AV_SAMPLE_FMT_NONE;
    }
}

static int delivered_bytes_per_sample(int32_t requested)
{
    return requested == FFAUDIO_SAMPLE_S16 ? 2 : requested == FFAUDIO_SAMPLE_S24 ? 3 : 4;
}

// Pack FFmpeg's left-aligned 24-in-32 PCM without losing significant bits.
static void pack_s24(const uint8_t *src, uint8_t *dst, int samples)
{
    for (int i = 0; i < samples; i++) {
        dst[0] = src[1];
        dst[1] = src[2];
        dst[2] = src[3];
        src += 4;
        dst += 3;
    }
}

static int ensure_buffers(ffaudio_decoder *dec, int frames)
{
    if (frames <= dec->scratch_frames)
        return FFAUDIO_OK;

    int scratch_bytes = frames * dec->swr_bytes_per_frame;
    int pending_bytes = frames * dec->out_bytes_per_frame;

    uint8_t *scratch = (uint8_t *)av_realloc(dec->scratch, (size_t)scratch_bytes);
    if (!scratch)
        return FFAUDIO_ERR_NO_MEMORY;
    dec->scratch = scratch;

    uint8_t *pending = (uint8_t *)av_realloc(dec->pending, (size_t)pending_bytes);
    if (!pending)
        return FFAUDIO_ERR_NO_MEMORY;
    dec->pending = pending;

    dec->scratch_frames = frames;
    dec->pending_capacity = pending_bytes;
    return FFAUDIO_OK;
}

// Convert one decoded frame into an empty pending buffer.
static int stage_frame(ffaudio_decoder *dec)
{
    int max_out = (int)swr_get_out_samples(dec->swr, dec->frame->nb_samples);
    if (max_out < 0)
        return max_out;

    int rc = ensure_buffers(dec, max_out);
    if (rc != FFAUDIO_OK)
        return rc;

    uint8_t *out[1] = { dec->scratch };
    int converted = swr_convert(dec->swr, out, max_out,
                                (const uint8_t **)dec->frame->extended_data,
                                dec->frame->nb_samples);
    if (converted < 0)
        return converted;

    if (dec->requested_format == FFAUDIO_SAMPLE_S24)
        pack_s24(dec->scratch, dec->pending, converted * dec->format.channels);
    else
        memcpy(dec->pending, dec->scratch, (size_t)converted * dec->out_bytes_per_frame);

    dec->pending_bytes = converted * dec->out_bytes_per_frame;
    dec->pending_offset = 0;

    if (dec->frame->pts != AV_NOPTS_VALUE) {
        AVRational tb = dec->fmt->streams[dec->stream_index]->time_base;
        dec->last_frame_ms = av_rescale_q(dec->frame->pts, tb, (AVRational){ 1, 1000 });
    }
    return FFAUDIO_OK;
}

// Drain swresample's delayed tail after the codec is exhausted.
static int stage_swr_tail(ffaudio_decoder *dec)
{
    int remaining = (int)swr_get_out_samples(dec->swr, 0);
    if (remaining <= 0)
        return FFAUDIO_EOF;

    int rc = ensure_buffers(dec, remaining);
    if (rc != FFAUDIO_OK)
        return rc;

    uint8_t *out[1] = { dec->scratch };
    int converted = swr_convert(dec->swr, out, remaining, NULL, 0);
    if (converted < 0)
        return converted;
    if (converted == 0)
        return FFAUDIO_EOF;

    if (dec->requested_format == FFAUDIO_SAMPLE_S24)
        pack_s24(dec->scratch, dec->pending, converted * dec->format.channels);
    else
        memcpy(dec->pending, dec->scratch, (size_t)converted * dec->out_bytes_per_frame);

    dec->pending_bytes = converted * dec->out_bytes_per_frame;
    dec->pending_offset = 0;
    return FFAUDIO_OK;
}

#define FFAUDIO_APE_FOOTER_BYTES 32
#define FFAUDIO_ID3V1_BYTES      128
#define FFAUDIO_TAG_DRAIN_LIMIT  (64 << 20)

static uint32_t read_le32(const uint8_t *p)
{
    return (uint32_t)p[0] | (uint32_t)p[1] << 8 | (uint32_t)p[2] << 16 | (uint32_t)p[3] << 24;
}

// Whether a failed read is a WavPack stream reaching its trailing APE tag.
// On seekable input FFmpeg's wv demuxer finds the tag at open and stops
// there. Without seeking it reads the tag as a block header and fails with
// AVERROR_INVALIDDATA after the last audio. This drains the rest of the
// stream and accepts it only if it is exactly one APE tag, optionally followed
// by ID3v1, starting where the last audio packet ended. Other damage still
// fails.
static int is_trailing_ape_tag(ffaudio_decoder *dec)
{
    if (dec->seekable || dec->last_packet_end < 0 || strcmp(dec->fmt->iformat->name, "wv") != 0)
        return 0;

    AVIOContext *pb = dec->fmt->pb;
    uint8_t tail[FFAUDIO_APE_FOOTER_BYTES + FFAUDIO_ID3V1_BYTES];
    uint8_t chunk[4096];
    int tail_bytes = 0;
    int64_t drained = 0;

    for (;;) {
        int read = avio_read(pb, chunk, sizeof(chunk));
        if (read <= 0)
            break;
        drained += read;
        if (drained > FFAUDIO_TAG_DRAIN_LIMIT)
            return 0;

        // Keep the last sizeof(tail) bytes.
        if (read >= (int)sizeof(tail)) {
            memcpy(tail, chunk + read - sizeof(tail), sizeof(tail));
            tail_bytes = sizeof(tail);
        } else {
            int keep = tail_bytes + read > (int)sizeof(tail) ? (int)sizeof(tail) - read : tail_bytes;
            memmove(tail, tail + tail_bytes - keep, keep);
            memcpy(tail + keep, chunk, read);
            tail_bytes = keep + read;
        }
    }

    int64_t end = avio_tell(pb);
    int id3v1 = tail_bytes >= FFAUDIO_APE_FOOTER_BYTES + FFAUDIO_ID3V1_BYTES
        && memcmp(tail + tail_bytes - FFAUDIO_ID3V1_BYTES, "TAG", 3) == 0;
    int footer_end = tail_bytes - (id3v1 ? FFAUDIO_ID3V1_BYTES : 0);
    if (footer_end < FFAUDIO_APE_FOOTER_BYTES)
        return 0;

    const uint8_t *footer = tail + footer_end - FFAUDIO_APE_FOOTER_BYTES;
    if (memcmp(footer, "APETAGEX", 8) != 0)
        return 0;

    // The size counts items and footer. Bit 31 of the flags marks a header.
    int64_t tag_bytes = read_le32(footer + 12);
    if (read_le32(footer + 20) & 0x80000000u)
        tag_bytes += FFAUDIO_APE_FOOTER_BYTES;

    int64_t tag_start = end - tag_bytes - (id3v1 ? FFAUDIO_ID3V1_BYTES : 0);
    return tag_start == dec->last_packet_end;
}

// Feed packets until one decoded frame is staged.
static int stage_next(ffaudio_decoder *dec)
{
    if (dec->finished)
        return FFAUDIO_EOF;

    for (;;) {
        int rc = avcodec_receive_frame(dec->codec, dec->frame);
        if (rc == 0) {
            int staged = stage_frame(dec);
            av_frame_unref(dec->frame);
            return staged;
        }
        if (rc == AVERROR_EOF) {
            dec->finished = 1;
            return stage_swr_tail(dec);
        }
        if (rc != AVERROR(EAGAIN))
            return rc;

        if (dec->packet_drained) {
            avcodec_send_packet(dec->codec, NULL);
            continue;
        }

        rc = av_read_frame(dec->fmt, dec->packet);
        if (rc == AVERROR_EOF || (rc == AVERROR_INVALIDDATA && is_trailing_ape_tag(dec))) {
            dec->packet_drained = 1;
            continue;
        }
        if (rc < 0)
            return rc;

        if (dec->packet->stream_index != dec->stream_index) {
            av_packet_unref(dec->packet);
            continue;
        }
        dec->last_packet_end = dec->packet->pos >= 0 ? dec->packet->pos + dec->packet->size : -1;

        rc = avcodec_send_packet(dec->codec, dec->packet);
        av_packet_unref(dec->packet);
        // Skip corrupt packets when the decoder can continue.
        if (rc < 0 && rc != AVERROR(EAGAIN) && rc != AVERROR_INVALIDDATA)
            return rc;
    }
}

// Populate attached-picture dimensions before stream analysis. The audio-only
// FFmpeg build cannot decode images, and missing dimensions trigger warnings.
static void size_attached_pictures(AVFormatContext *fmt);

// Read dimensions from the first JPEG start-of-frame marker.
static int jpeg_size(const uint8_t *d, int n, int *w, int *h)
{
    if (n < 4 || d[0] != 0xFF || d[1] != 0xD8)
        return 0;

    int i = 2;
    while (i + 3 < n) {
        if (d[i] != 0xFF) {
            i++;
            continue;
        }
        uint8_t marker = d[i + 1];
        if (marker == 0xFF)
            continue;
        if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) {
            i += 2;
            continue;
        }

        int len = (d[i + 2] << 8) | d[i + 3];
        // DHT, JPG, and DAC share the marker range but are not frame headers.
        if (marker >= 0xC0 && marker <= 0xCF
            && marker != 0xC4 && marker != 0xC8 && marker != 0xCC) {
            if (i + 9 >= n)
                return 0;
            *h = (d[i + 5] << 8) | d[i + 6];
            *w = (d[i + 7] << 8) | d[i + 8];
            return *w > 0 && *h > 0;
        }
        if (len < 2)
            return 0;
        i += 2 + len;
    }
    return 0;
}

// PNG requires IHDR to be the first chunk.
static int png_size(const uint8_t *d, int n, int *w, int *h)
{
    static const uint8_t sig[8] = { 0x89, 'P', 'N', 'G', 0x0D, 0x0A, 0x1A, 0x0A };
    if (n < 24 || memcmp(d, sig, sizeof(sig)) != 0 || memcmp(d + 12, "IHDR", 4) != 0)
        return 0;

    *w = (d[16] << 24) | (d[17] << 16) | (d[18] << 8) | d[19];
    *h = (d[20] << 24) | (d[21] << 16) | (d[22] << 8) | d[23];
    return *w > 0 && *h > 0;
}

// GIF and BMP dimensions are little-endian at fixed offsets.
static int gif_size(const uint8_t *d, int n, int *w, int *h)
{
    if (n < 10 || memcmp(d, "GIF8", 4) != 0)
        return 0;

    *w = d[6] | (d[7] << 8);
    *h = d[8] | (d[9] << 8);
    return *w > 0 && *h > 0;
}

static int bmp_size(const uint8_t *d, int n, int *w, int *h)
{
    if (n < 26 || d[0] != 'B' || d[1] != 'M')
        return 0;

    *w = (int)((uint32_t)d[18] | ((uint32_t)d[19] << 8) | ((uint32_t)d[20] << 16) | ((uint32_t)d[21] << 24));
    int height = (int)((uint32_t)d[22] | ((uint32_t)d[23] << 8) | ((uint32_t)d[24] << 16) | ((uint32_t)d[25] << 24));
    // Negative BMP heights indicate top-down row order.
    *h = height < 0 ? -height : height;
    return *w > 0 && *h > 0;
}

static void size_attached_pictures(AVFormatContext *fmt)
{
    for (unsigned i = 0; i < fmt->nb_streams; i++) {
        AVStream *stream = fmt->streams[i];
        AVCodecParameters *par = stream->codecpar;

        if (!(stream->disposition & AV_DISPOSITION_ATTACHED_PIC))
            continue;
        if (par->width > 0 && par->height > 0)
            continue;

        const uint8_t *data = stream->attached_pic.data;
        int size = stream->attached_pic.size;
        if (!data || size <= 0)
            continue;

        int w = 0, h = 0;
        int known = 0;
        switch (par->codec_id) {
            case AV_CODEC_ID_MJPEG: known = jpeg_size(data, size, &w, &h); break;
            case AV_CODEC_ID_PNG:   known = png_size(data, size, &w, &h);  break;
            case AV_CODEC_ID_GIF:   known = gif_size(data, size, &w, &h);  break;
            case AV_CODEC_ID_BMP:   known = bmp_size(data, size, &w, &h);  break;
            default: break;
        }

        if (known) {
            par->width = w;
            par->height = h;
        }
    }
}

static int finish_open(ffaudio_decoder *dec)
{
    int stream = av_find_best_stream(dec->fmt, AVMEDIA_TYPE_AUDIO, -1, -1, NULL, 0);
    if (stream < 0)
        return FFAUDIO_ERR_NO_AUDIO;
    dec->stream_index = stream;

    AVCodecParameters *par = dec->fmt->streams[stream]->codecpar;
    const AVCodec *codec = avcodec_find_decoder(par->codec_id);
    if (!codec)
        return FFAUDIO_ERR_NO_AUDIO;

    dec->codec = avcodec_alloc_context3(codec);
    if (!dec->codec)
        return FFAUDIO_ERR_NO_MEMORY;

    int rc = avcodec_parameters_to_context(dec->codec, par);
    if (rc < 0)
        return rc;

    dec->codec->pkt_timebase = dec->fmt->streams[stream]->time_base;
    rc = avcodec_open2(dec->codec, codec, NULL);
    if (rc < 0)
        return rc;

    int source_rate = dec->codec->sample_rate;
    int source_channels = dec->codec->ch_layout.nb_channels;
    int out_rate = dec->requested_rate > 0 ? dec->requested_rate : source_rate;
    int out_channels = dec->requested_channels > 0 ? dec->requested_channels : source_channels;

    av_channel_layout_default(&dec->out_layout, out_channels);

    rc = swr_alloc_set_opts2(&dec->swr,
                             &dec->out_layout, dec->swr_format, out_rate,
                             &dec->codec->ch_layout, dec->codec->sample_fmt, source_rate,
                             0, NULL);
    if (rc < 0)
        return rc;

    rc = swr_init(dec->swr);
    if (rc < 0)
        return rc;

    dec->swr_bytes_per_frame = av_get_bytes_per_sample(dec->swr_format) * out_channels;
    dec->out_bytes_per_frame = delivered_bytes_per_sample(dec->requested_format) * out_channels;

    // Preserve the significant depth of formats such as 24-bit PCM in 32-bit words.
    int depth = par->bits_per_raw_sample;
    if (depth <= 0)
        depth = av_get_bytes_per_sample(dec->codec->sample_fmt) * 8;

    dec->format.sample_rate = out_rate;
    dec->format.channels = out_channels;
    dec->format.sample_format = dec->requested_format;
    dec->format.source_bit_depth = depth;
    dec->format.source_sample_rate = source_rate;
    dec->format.source_channels = source_channels;
    dec->format.duration_ms = dec->fmt->duration == AV_NOPTS_VALUE
        ? -1
        : av_rescale_q(dec->fmt->duration, AV_TIME_BASE_Q, (AVRational){ 1, 1000 });

    dec->packet = av_packet_alloc();
    dec->frame = av_frame_alloc();
    if (!dec->packet || !dec->frame)
        return FFAUDIO_ERR_NO_MEMORY;

    return FFAUDIO_OK;
}

static int alloc_decoder(int32_t requested_format,
                         int32_t requested_rate,
                         int32_t requested_channels,
                         ffaudio_decoder **out_decoder)
{
    if (!out_decoder)
        return FFAUDIO_ERR_ARGUMENT;
    *out_decoder = NULL;

    enum AVSampleFormat swr_format = swr_format_for(requested_format);
    if (swr_format == AV_SAMPLE_FMT_NONE || requested_rate < 0 || requested_channels < 0)
        return FFAUDIO_ERR_ARGUMENT;

    ffaudio_decoder *dec = (ffaudio_decoder *)av_mallocz(sizeof(ffaudio_decoder));
    if (!dec)
        return FFAUDIO_ERR_NO_MEMORY;

    dec->stream_index = -1;
    dec->last_frame_ms = 0;
    dec->last_packet_end = -1;
    dec->swr_format = swr_format;
    dec->requested_format = requested_format;
    dec->requested_rate = requested_rate;
    dec->requested_channels = requested_channels;

    *out_decoder = dec;
    return FFAUDIO_OK;
}

FFAUDIO_API int ffaudio_decoder_open_path(const char *path,
                                        int32_t requested_format,
                                        int32_t requested_sample_rate,
                                        int32_t requested_channels,
                                        ffaudio_decoder **out_decoder)
{
    if (!path)
        return FFAUDIO_ERR_ARGUMENT;

    ffaudio_decoder *dec = NULL;
    int rc = alloc_decoder(requested_format, requested_sample_rate, requested_channels, &dec);
    if (rc != FFAUDIO_OK)
        return rc;

    dec->seekable = 1;
    rc = avformat_open_input(&dec->fmt, path, NULL, NULL);
    if (rc < 0)
        goto fail;

    size_attached_pictures(dec->fmt);

    rc = avformat_find_stream_info(dec->fmt, NULL);
    if (rc < 0)
        goto fail;

    rc = finish_open(dec);
    if (rc != FFAUDIO_OK)
        goto fail;

    *out_decoder = dec;
    return FFAUDIO_OK;

fail:
    ffaudio_decoder_close(dec);
    *out_decoder = NULL;
    return rc;
}

FFAUDIO_API int ffaudio_decoder_open_io(void *opaque,
                                      ffaudio_read_fn read,
                                      ffaudio_seek_fn seek,
                                      int64_t size,
                                      int32_t seekable,
                                      const char *format_hint,
                                      int32_t requested_format,
                                      int32_t requested_sample_rate,
                                      int32_t requested_channels,
                                      ffaudio_decoder **out_decoder)
{
    if (!read)
        return FFAUDIO_ERR_ARGUMENT;

    ffaudio_decoder *dec = NULL;
    int rc = alloc_decoder(requested_format, requested_sample_rate, requested_channels, &dec);
    if (rc != FFAUDIO_OK)
        return rc;

    dec->io_opaque = opaque;
    dec->io_read = read;
    dec->io_seek = seek;
    dec->seekable = seek != NULL && seekable != 0;

    uint8_t *io_buffer = (uint8_t *)av_malloc(FFAUDIO_IO_BUFFER_BYTES);
    if (!io_buffer) {
        rc = FFAUDIO_ERR_NO_MEMORY;
        goto fail;
    }

    dec->avio = avio_alloc_context(io_buffer, FFAUDIO_IO_BUFFER_BYTES, 0, dec,
                                   io_read_packet, NULL,
                                   dec->seekable ? io_seek : NULL);
    if (!dec->avio) {
        av_free(io_buffer);
        rc = FFAUDIO_ERR_NO_MEMORY;
        goto fail;
    }
    // Prevent FFmpeg from attempting seeks on forward-only input.
    dec->avio->seekable = dec->seekable ? AVIO_SEEKABLE_NORMAL : 0;

    dec->fmt = avformat_alloc_context();
    if (!dec->fmt) {
        rc = FFAUDIO_ERR_NO_MEMORY;
        goto fail;
    }
    dec->fmt->pb = dec->avio;
    dec->fmt->flags |= AVFMT_FLAG_CUSTOM_IO;

    const AVInputFormat *forced = NULL;
    if (format_hint && format_hint[0])
        forced = av_find_input_format(format_hint);

    rc = avformat_open_input(&dec->fmt, NULL, forced, NULL);
    if (rc < 0)
        goto fail;

    size_attached_pictures(dec->fmt);

    rc = avformat_find_stream_info(dec->fmt, NULL);
    if (rc < 0)
        goto fail;

    rc = finish_open(dec);
    if (rc != FFAUDIO_OK)
        goto fail;

    (void)size;
    *out_decoder = dec;
    return FFAUDIO_OK;

fail:
    ffaudio_decoder_close(dec);
    *out_decoder = NULL;
    return rc;
}

FFAUDIO_API int ffaudio_decoder_get_format(ffaudio_decoder *decoder, ffaudio_decoder_format *out_format)
{
    if (!decoder || !out_format)
        return FFAUDIO_ERR_ARGUMENT;
    *out_format = decoder->format;
    return FFAUDIO_OK;
}

FFAUDIO_API int ffaudio_decoder_read(ffaudio_decoder *decoder,
                                   uint8_t *buffer,
                                   int32_t buffer_bytes,
                                   int32_t *out_bytes)
{
    if (!decoder || !buffer || buffer_bytes < 0 || !out_bytes)
        return FFAUDIO_ERR_ARGUMENT;

    *out_bytes = 0;
    int written = 0;

    if (decoder->deferred_error) {
        int deferred = decoder->deferred_error;
        decoder->deferred_error = 0;
        return deferred;
    }

    while (written < buffer_bytes) {
        int available = decoder->pending_bytes - decoder->pending_offset;
        if (available > 0) {
            int take = buffer_bytes - written;
            if (take > available)
                take = available;
            memcpy(buffer + written, decoder->pending + decoder->pending_offset, (size_t)take);
            decoder->pending_offset += take;
            written += take;
            continue;
        }

        int rc = stage_next(decoder);
        if (rc == FFAUDIO_EOF) {
            *out_bytes = written;
            return written > 0 ? FFAUDIO_OK : FFAUDIO_EOF;
        }
        if (rc != FFAUDIO_OK) {
            // Return valid decoded bytes now and the error on the next read,
            // which cannot rely on the source failing a second time.
            if (written > 0) {
                decoder->deferred_error = rc;
                *out_bytes = written;
                return FFAUDIO_OK;
            }
            return rc;
        }
    }

    *out_bytes = written;
    return FFAUDIO_OK;
}

FFAUDIO_API int ffaudio_decoder_seek(ffaudio_decoder *decoder, int64_t position_ms, int64_t *out_landed_ms)
{
    if (!decoder || position_ms < 0)
        return FFAUDIO_ERR_ARGUMENT;
    if (!decoder->seekable)
        return FFAUDIO_ERR_IO;

    AVRational tb = decoder->fmt->streams[decoder->stream_index]->time_base;
    int64_t ts = av_rescale_q(position_ms, (AVRational){ 1, 1000 }, tb);

    int rc = av_seek_frame(decoder->fmt, decoder->stream_index, ts, AVSEEK_FLAG_BACKWARD);
    if (rc < 0)
        return rc;

    avcodec_flush_buffers(decoder->codec);
    decoder->pending_bytes = 0;
    decoder->pending_offset = 0;
    decoder->packet_drained = 0;
    decoder->finished = 0;
    decoder->deferred_error = 0;
    decoder->last_frame_ms = position_ms;

    // Rebuild swresample to discard delayed samples from before the seek.
    swr_free(&decoder->swr);
    AVChannelLayout out_layout;
    av_channel_layout_default(&out_layout, decoder->format.channels);
    rc = swr_alloc_set_opts2(&decoder->swr,
                             &out_layout, decoder->swr_format, decoder->format.sample_rate,
                             &decoder->codec->ch_layout, decoder->codec->sample_fmt,
                             decoder->format.source_sample_rate,
                             0, NULL);
    av_channel_layout_uninit(&out_layout);
    if (rc < 0)
        return rc;
    rc = swr_init(decoder->swr);
    if (rc < 0)
        return rc;

    // Decode one frame to determine the demuxer's actual landing position.
    rc = stage_next(decoder);
    if (rc != FFAUDIO_OK && rc != FFAUDIO_EOF)
        return rc;

    if (out_landed_ms)
        *out_landed_ms = decoder->last_frame_ms;
    return FFAUDIO_OK;
}

FFAUDIO_API void ffaudio_decoder_close(ffaudio_decoder *decoder)
{
    if (!decoder)
        return;

    if (decoder->frame)
        av_frame_free(&decoder->frame);
    if (decoder->packet)
        av_packet_free(&decoder->packet);
    if (decoder->codec)
        avcodec_free_context(&decoder->codec);
    if (decoder->swr)
        swr_free(&decoder->swr);
    if (decoder->fmt)
        avformat_close_input(&decoder->fmt);
    if (decoder->avio) {
        // FFmpeg may replace the buffer; avio_context_free does not free it.
        av_freep(&decoder->avio->buffer);
        avio_context_free(&decoder->avio);
    }
    av_channel_layout_uninit(&decoder->out_layout);
    av_freep(&decoder->scratch);
    av_freep(&decoder->pending);
    av_free(decoder);
}

// Copy into caller-owned storage and report truncation.
static int copy_string(char *buffer, int32_t buffer_bytes, const char *value)
{
    if (!buffer || buffer_bytes <= 0)
        return FFAUDIO_OK;
    if (!value)
        value = "";

    size_t length = strlen(value);
    if (length >= (size_t)buffer_bytes) {
        memcpy(buffer, value, (size_t)buffer_bytes - 1);
        buffer[buffer_bytes - 1] = '\0';
        return FFAUDIO_ERR_TRUNCATED;
    }

    memcpy(buffer, value, length + 1);
    return FFAUDIO_OK;
}

// av_dict_get supports older FFmpeg versions than av_dict_iterate.
static int dict_count(const AVDictionary *dict)
{
    int count = 0;
    const AVDictionaryEntry *entry = NULL;
    while ((entry = av_dict_get(dict, "", entry, AV_DICT_IGNORE_SUFFIX)) != NULL)
        count++;
    return count;
}

static const AVDictionaryEntry *dict_at(const AVDictionary *dict, int index)
{
    const AVDictionaryEntry *entry = NULL;
    while ((entry = av_dict_get(dict, "", entry, AV_DICT_IGNORE_SUFFIX)) != NULL) {
        if (index-- == 0)
            return entry;
    }
    return NULL;
}

// Read container tags first, then audio-stream tags.
static const AVDictionary *tag_source(ffaudio_decoder *dec, int which)
{
    if (which == 0)
        return dec->fmt->metadata;
    return dec->fmt->streams[dec->stream_index]->metadata;
}

FFAUDIO_API int ffaudio_decoder_tag_count(ffaudio_decoder *decoder, int32_t *out_count)
{
    if (!decoder || !out_count)
        return FFAUDIO_ERR_ARGUMENT;

    *out_count = (int32_t)(dict_count(tag_source(decoder, 0))
                         + dict_count(tag_source(decoder, 1)));
    return FFAUDIO_OK;
}

FFAUDIO_API int ffaudio_decoder_tag_at(ffaudio_decoder *decoder,
                                     int32_t index,
                                     char *key, int32_t key_bytes,
                                     char *value, int32_t value_bytes)
{
    if (!decoder || index < 0)
        return FFAUDIO_ERR_ARGUMENT;

    int container = dict_count(tag_source(decoder, 0));
    const AVDictionaryEntry *entry = index < container
        ? dict_at(tag_source(decoder, 0), index)
        : dict_at(tag_source(decoder, 1), index - container);

    if (!entry)
        return FFAUDIO_ERR_NOT_PRESENT;

    int rc = copy_string(key, key_bytes, entry->key);
    int rc2 = copy_string(value, value_bytes, entry->value);
    return rc != FFAUDIO_OK ? rc : rc2;
}

// Attached pictures are already-demuxed packets on flagged video streams.
static const AVStream *attached_picture(ffaudio_decoder *dec)
{
    for (unsigned i = 0; i < dec->fmt->nb_streams; i++) {
        const AVStream *stream = dec->fmt->streams[i];
        if ((stream->disposition & AV_DISPOSITION_ATTACHED_PIC) && stream->attached_pic.size > 0)
            return stream;
    }
    return NULL;
}

static const char *picture_mime(const AVStream *stream)
{
    // Prefer the container's more specific MIME declaration.
    const AVDictionaryEntry *declared = av_dict_get(stream->metadata, "mimetype", NULL, 0);
    if (declared && declared->value && declared->value[0])
        return declared->value;

    switch (stream->codecpar->codec_id) {
        case AV_CODEC_ID_MJPEG:  return "image/jpeg";
        case AV_CODEC_ID_PNG:    return "image/png";
        case AV_CODEC_ID_BMP:    return "image/bmp";
        case AV_CODEC_ID_GIF:    return "image/gif";
        case AV_CODEC_ID_WEBP:   return "image/webp";
        default:                 return "application/octet-stream";
    }
}

FFAUDIO_API int ffaudio_decoder_cover_art(ffaudio_decoder *decoder,
                                        uint8_t *buffer, int32_t buffer_bytes,
                                        int32_t *out_bytes,
                                        char *mime, int32_t mime_bytes,
                                        int32_t *out_width, int32_t *out_height)
{
    if (!decoder)
        return FFAUDIO_ERR_ARGUMENT;

    const AVStream *stream = attached_picture(decoder);
    if (!stream)
        return FFAUDIO_ERR_NOT_PRESENT;

    // Zero dimensions mean the image header was unsupported.
    if (out_width)
        *out_width = stream->codecpar->width;
    if (out_height)
        *out_height = stream->codecpar->height;

    int size = stream->attached_pic.size;
    if (out_bytes)
        *out_bytes = (int32_t)size;

    int rc = copy_string(mime, mime_bytes, picture_mime(stream));

    // A NULL buffer is the supported size-query operation.
    if (!buffer)
        return rc;

    // Never return a partial encoded image.
    if (buffer_bytes < size)
        return FFAUDIO_ERR_TRUNCATED;

    memcpy(buffer, stream->attached_pic.data, (size_t)size);
    return rc;
}

FFAUDIO_API int ffaudio_decoder_channel_layout(ffaudio_decoder *decoder,
                                             char *buffer, int32_t buffer_bytes)
{
    if (!decoder || !buffer || buffer_bytes <= 0)
        return FFAUDIO_ERR_ARGUMENT;

    int rc = av_channel_layout_describe(&decoder->out_layout, buffer, (size_t)buffer_bytes);
    if (rc < 0)
        return rc;
    return rc > buffer_bytes ? FFAUDIO_ERR_TRUNCATED : FFAUDIO_OK;
}

FFAUDIO_API int ffaudio_decoder_names(ffaudio_decoder *decoder,
                                    char *codec, int32_t codec_bytes,
                                    char *container, int32_t container_bytes)
{
    if (!decoder)
        return FFAUDIO_ERR_ARGUMENT;

    const char *codec_name = avcodec_get_name(decoder->codec->codec_id);
    const char *container_name = decoder->fmt->iformat ? decoder->fmt->iformat->name : NULL;

    int rc = copy_string(codec, codec_bytes, codec_name);
    int rc2 = copy_string(container, container_bytes, container_name);
    return rc != FFAUDIO_OK ? rc : rc2;
}

FFAUDIO_API void ffaudio_error_string(int code, char *buffer, int32_t buffer_bytes)
{
    if (!buffer || buffer_bytes <= 0)
        return;

    const char *own = NULL;
    switch (code) {
        case FFAUDIO_OK:            own = "ok"; break;
        case FFAUDIO_EOF:           own = "end of stream"; break;
        case FFAUDIO_ERR_ARGUMENT:  own = "invalid argument"; break;
        case FFAUDIO_ERR_NO_AUDIO:  own = "no decodable audio stream"; break;
        case FFAUDIO_ERR_NO_MEMORY: own = "out of memory"; break;
        case FFAUDIO_ERR_ABI:       own = "abi version mismatch"; break;
        case FFAUDIO_ERR_IO:        own = "stream does not support seeking"; break;
        case FFAUDIO_ERR_NOT_PRESENT: own = "this file does not carry that"; break;
        case FFAUDIO_ERR_TRUNCATED: own = "the buffer given was too small"; break;
        default: break;
    }

    if (own) {
        snprintf(buffer, (size_t)buffer_bytes, "%s", own);
        return;
    }
    if (av_strerror(code, buffer, (size_t)buffer_bytes) < 0)
        snprintf(buffer, (size_t)buffer_bytes, "ffmpeg error %d", code);
}

FFAUDIO_API int32_t ffaudio_abi_version(void)
{
    return FFAUDIO_ABI_VERSION;
}

FFAUDIO_API int ffaudio_ffmpeg_license(char *buffer, int32_t buffer_bytes)
{
    return copy_string(buffer, buffer_bytes, avutil_license());
}

FFAUDIO_API int ffaudio_ffmpeg_configuration(char *buffer, int32_t buffer_bytes)
{
    return copy_string(buffer, buffer_bytes, avutil_configuration());
}

// Fall back to the numeric library version when no source version is embedded.
FFAUDIO_API int ffaudio_ffmpeg_version(char *buffer, int32_t buffer_bytes)
{
    const char *info = av_version_info();
    if (info && *info)
        return copy_string(buffer, buffer_bytes, info);

    char fallback[32];
    unsigned version = avutil_version();
    snprintf(fallback, sizeof(fallback), "libavutil %u.%u.%u",
             version >> 16, (version >> 8) & 0xff, version & 0xff);
    return copy_string(buffer, buffer_bytes, fallback);
}
