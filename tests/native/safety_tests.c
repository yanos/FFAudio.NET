// Include the implementation to exercise private size/parser helpers without
// adding test-only exports to the shipping ABI.
#include "../../native/ffaudio.c"

#define CHECK(condition) do { \
    if (!(condition)) { \
        fprintf(stderr, "%s:%d: %s\n", __FILE__, __LINE__, #condition); \
        return 1; \
    } \
} while (0)

static int buffer_sizes(void)
{
    ffaudio_decoder dec = {0};
    dec.swr_bytes_per_frame = 8;
    dec.out_bytes_per_frame = 6;
    CHECK(ensure_buffers(&dec, -1) == FFAUDIO_ERR_ARGUMENT);
    CHECK(ensure_buffers(&dec, INT_MAX / 8 + 1) == FFAUDIO_ERR_NO_MEMORY);
    CHECK(ensure_buffers(&dec, 536870913) == FFAUDIO_ERR_NO_MEMORY);
    CHECK(!dec.scratch && !dec.pending && dec.scratch_frames == 0);
    // Check both products independently, even if a future format is wider.
    dec.swr_bytes_per_frame = 2;
    CHECK(ensure_buffers(&dec, INT_MAX / 6 + 1) == FFAUDIO_ERR_NO_MEMORY);
    dec.swr_bytes_per_frame = 0;
    CHECK(ensure_buffers(&dec, 0) == FFAUDIO_ERR_ARGUMENT);
    dec.swr_bytes_per_frame = 8;
    CHECK(ensure_buffers(&dec, 0) == FFAUDIO_OK);
    CHECK(ensure_buffers(&dec, 16) == FFAUDIO_OK);
    memset(dec.scratch, 0x5a, 16 * 8);
    memset(dec.pending, 0xa5, 16 * 6);
    CHECK(ensure_buffers(&dec, 32) == FFAUDIO_OK);
    CHECK(dec.scratch_frames == 32 && dec.pending_capacity == 32 * 6);
    CHECK(dec.scratch[16 * 8 - 1] == 0x5a && dec.pending[16 * 6 - 1] == 0xa5);
    CHECK(ensure_buffers(&dec, INT_MAX) == FFAUDIO_ERR_NO_MEMORY);
    CHECK(dec.scratch_frames == 32 && dec.pending_capacity == 32 * 6);
    av_free(dec.scratch);
    av_free(dec.pending);
    return 0;
}

static int picture_headers(void)
{
    int w = 0, h = 0;
    const uint8_t repeated_marker[] = {0xff, 0xd8, 0xff, 0xff, 0, 0};
    CHECK(!jpeg_size(repeated_marker, sizeof(repeated_marker), &w, &h));
    const uint8_t jpeg[] = {
        0xff, 0xd8, 0xff, 0xff, 0xc0, 0, 11, 8, 0, 2, 0, 3, 1, 1, 0x11, 0
    };
    CHECK(jpeg_size(jpeg, sizeof(jpeg), &w, &h) && w == 3 && h == 2);
    for (int n = 0; n < (int)sizeof(jpeg); n++)
        CHECK(!jpeg_size(jpeg, n, &w, &h));
    uint8_t invalid_segment[sizeof(jpeg)];
    memcpy(invalid_segment, jpeg, sizeof(jpeg));
    invalid_segment[6] = 7;
    CHECK(!jpeg_size(invalid_segment, sizeof(invalid_segment), &w, &h));
    invalid_segment[6] = 255;
    CHECK(!jpeg_size(invalid_segment, sizeof(invalid_segment), &w, &h));

    uint8_t png[24] = {0x89, 'P', 'N', 'G', 13, 10, 26, 10};
    memcpy(png + 12, "IHDR", 4);
    png[19] = 3;
    png[23] = 2;
    CHECK(png_size(png, sizeof(png), &w, &h) && w == 3 && h == 2);
    for (int n = 0; n < (int)sizeof(png); n++)
        CHECK(!png_size(png, n, &w, &h));
    png[16] = 0x80;
    CHECK(!png_size(png, sizeof(png), &w, &h));
    png[16] = 0;
    png[20] = 0x80;
    CHECK(!png_size(png, sizeof(png), &w, &h));

    uint8_t bmp[26] = {'B', 'M'};
    bmp[18] = 3;
    bmp[22] = 2;
    CHECK(bmp_size(bmp, sizeof(bmp), &w, &h) && w == 3 && h == 2);
    memset(bmp + 22, 0xff, 4);
    bmp[22] = 0xfe;
    CHECK(bmp_size(bmp, sizeof(bmp), &w, &h) && w == 3 && h == 2);
    memset(bmp + 22, 0, 4);
    bmp[25] = 0x80;
    CHECK(!bmp_size(bmp, sizeof(bmp), &w, &h));
    for (int n = 0; n < (int)sizeof(bmp); n++)
        CHECK(!bmp_size(bmp, n, &w, &h));
    return 0;
}

static int changed_frame(void)
{
    // No sample storage or resampler: incompatible metadata must be rejected
    // before any attempt to read sample planes.
    ffaudio_decoder dec = {0};
    dec.frame = av_frame_alloc();
    CHECK(dec.frame);
    dec.in_format = AV_SAMPLE_FMT_FLTP;
    dec.in_rate = 44100;
    av_channel_layout_default(&dec.in_layout, 2);
    for (int change = 0; change < 3; change++) {
        av_frame_unref(dec.frame);
        dec.input_changed = 0;
        dec.frame->format = change == 0 ? AV_SAMPLE_FMT_S16 : dec.in_format;
        dec.frame->sample_rate = change == 1 ? 48000 : dec.in_rate;
        av_channel_layout_default(&dec.frame->ch_layout, change == 2 ? 1 : 2);
        CHECK(stage_frame(&dec) == AVERROR_INVALIDDATA);
        CHECK(dec.input_changed);
        CHECK(stage_next(&dec) == AVERROR_INVALIDDATA);
    }
    av_frame_free(&dec.frame);
    av_channel_layout_uninit(&dec.in_layout);
    return 0;
}

static int file_read(void *opaque, uint8_t *buffer, int bytes)
{
    FILE *file = opaque;
    int count = (int)fread(buffer, 1, (size_t)bytes, file);
    return ferror(file) ? -1 : count;
}

static int64_t file_seek(void *opaque, int64_t offset, int whence)
{
    FILE *file = opaque;
    if (whence == FFAUDIO_SEEK_SIZE) {
        long position = ftell(file);
        if (position < 0 || fseek(file, 0, SEEK_END))
            return -1;
        long size = ftell(file);
        return fseek(file, position, SEEK_SET) ? -1 : size;
    }
    // Fixtures are tiny; do not narrow an arbitrary ABI offset to long.
    if (offset < LONG_MIN || offset > LONG_MAX || fseek(file, (long)offset, whence))
        return -1;
    return ftell(file);
}

static int changed_file(const char *path)
{
    for (int format = FFAUDIO_SAMPLE_S16; format <= FFAUDIO_SAMPLE_F32; format++) {
        for (int custom_io = 0; custom_io < 2; custom_io++) {
            for (int resample = 0; resample < 2; resample++) {
                ffaudio_decoder *dec = NULL;
                FILE *file = NULL;
                int rate = resample ? 48000 : 0;
                int rc;
                if (custom_io) {
                    file = fopen(path, "rb");
                    CHECK(file);
                    rc = ffaudio_decoder_open_io(file, file_read, file_seek, -1, 1,
                                                NULL, format, rate, 0, &dec);
                } else {
                    rc = ffaudio_decoder_open_path(path, format, rate, 0, &dec);
                }
                CHECK(rc == FFAUDIO_OK);
                CHECK(dec->format.channels == 2);
                uint8_t buffer[16384];
                int written;
                int total = 0;
                for (int reads = 0; reads < 10000; reads++) {
                    rc = ffaudio_decoder_read(dec, buffer, sizeof(buffer), &written);
                    if (rc != FFAUDIO_OK)
                        break;
                    CHECK(written > 0 && written <= (int)sizeof(buffer));
                    total += written;
                }
                CHECK(total > 0 && rc == AVERROR_INVALIDDATA);
                CHECK(ffaudio_decoder_read(dec, buffer, sizeof(buffer), &written) == AVERROR_INVALIDDATA);
                // The codec context now describes mono. Seeking must restore
                // the original input contract instead of copying that state.
                int64_t landed;
                CHECK(ffaudio_decoder_seek(dec, 0, &landed) == FFAUDIO_OK);
                CHECK(ffaudio_decoder_read(dec, buffer, sizeof(buffer), &written) == FFAUDIO_OK);
                CHECK(written > 0);
                ffaudio_decoder_close(dec);
                if (file)
                    fclose(file);
            }
        }
    }
    return 0;
}

static int cover_file(const char *path)
{
    ffaudio_decoder *dec = NULL;
    CHECK(ffaudio_decoder_open_path(path, FFAUDIO_SAMPLE_F32, 0, 0, &dec) == FFAUDIO_OK);
    int bytes, width, height;
    CHECK(ffaudio_decoder_cover_art(dec, NULL, 0, &bytes, NULL, 0, &width, &height) == FFAUDIO_OK);
    CHECK(bytes == 6 && width == 0 && height == 0);
    uint8_t buffer[16384];
    int written;
    CHECK(ffaudio_decoder_read(dec, buffer, sizeof(buffer), &written) == FFAUDIO_OK);
    CHECK(written > 0);
    ffaudio_decoder_close(dec);
    return 0;
}

int main(int argc, char **argv)
{
    if (argc == 3 && !strcmp(argv[1], "changed-file"))
        return changed_file(argv[2]);
    if (argc == 3 && !strcmp(argv[1], "cover-file"))
        return cover_file(argv[2]);
    CHECK(argc == 1);
    CHECK(buffer_sizes() == 0);
    CHECK(picture_headers() == 0);
    CHECK(changed_frame() == 0);
    return 0;
}
