#!/usr/bin/env bash
# Shared component sets for static FFmpeg builds.
# slim targets music-library formats; full enables every audio decoder and
# demuxer without enabling video decoders.
ffaudio_slim_decoders="mp3,mp3float,aac,aac_latm,alac,flac,vorbis,opus,wavpack,ape,dsd_lsbf,dsd_msbf,dsd_lsbf_planar,dsd_msbf_planar,pcm_s16le,pcm_s16be,pcm_s24le,pcm_s24be,pcm_s32le,pcm_u8,pcm_f32le,pcm_f64le"
ffaudio_slim_demuxers="mov,mp3,flac,wav,w64,ogg,matroska,aac,ape,wv,aiff,dsf"
ffaudio_slim_parsers="mpegaudio,aac,aac_latm,flac,vorbis,opus"

# FFmpeg version used by every source build.
ffaudio_ffmpeg_version="${FFAUDIO_FFMPEG_VERSION:-9.0.2}"

ffaudio_variant="${FFAUDIO_VARIANT:-slim}"
case "$ffaudio_variant" in
    slim|full) ;;
    *) echo "--variant (FFAUDIO_VARIANT) must be 'slim' or 'full', not '$ffaudio_variant'." >&2; return 1 2>/dev/null || exit 1 ;;
esac

# Optional whitespace-separated configure flags; individual values cannot contain spaces.
ffaudio_extra_configure_flags=()
if [ -n "${FFAUDIO_FFMPEG_CONFIGURE_FLAGS:-}" ]; then
    read -r -a ffaudio_extra_configure_flags <<< "$FFAUDIO_FFMPEG_CONFIGURE_FLAGS"
fi

# Keep reusable builds separate by version, variant, and architecture.
ffaudio_prefix_root() {
    echo "$1/prefix/$ffaudio_ffmpeg_version/$ffaudio_variant"
}

# Extract audio decoder names from FFmpeg's grouped declarations. Fail if its
# section markers change instead of silently producing an empty build.
ffaudio_audio_decoders() {
    local source_dir="$1"
    local list="$source_dir/libavcodec/allcodecs.c"
    [ -f "$list" ] || { echo "No allcodecs.c under $source_dir" >&2; return 1; }

    local names
    names="$(awk '/^\/\* audio codecs \*\/$/{on=1} /^\/\* subtitles \*\/$/{on=0} on' "$list" \
        | sed -n 's/^extern const FFCodec ff_\([a-z0-9_]*\)_decoder;.*/\1/p' \
        | paste -sd, -)"

    if [ -z "$names" ]; then
        echo "Found no audio decoders in $list - its section markers must have changed." >&2
        return 1
    fi

    echo "$names"
}

# Print target-independent configure arguments for the selected variant.
ffaudio_component_flags() {
    local source_dir="$1"

    printf '%s\n' --disable-everything

    if [ "$ffaudio_variant" = slim ]; then
        printf '%s\n' \
            "--enable-decoder=$ffaudio_slim_decoders" \
            "--enable-demuxer=$ffaudio_slim_demuxers" \
            "--enable-parser=$ffaudio_slim_parsers"
    else
        local decoders
        decoders="$(ffaudio_audio_decoders "$source_dir")" || return 1
        # FFmpeg drops demuxers whose disabled dependencies are unavailable.
        printf '%s\n' \
            "--enable-decoder=$decoders" \
            "--enable-demuxer=*" \
            "--enable-parser=*"
    fi

    printf '%s\n' --enable-protocol=file
}

# Require explicit acknowledgement for GPL or nonfree configurations.
ffaudio_assert_lgpl() {
    local build_dir="$1"
    local header="$build_dir/config.h"
    [ -f "$header" ] || { echo "No config.h under $build_dir - configure did not finish." >&2; return 1; }

    local flag
    for flag in GPL NONFREE; do
        if grep -q "^#define CONFIG_$flag 1\$" "$header"; then
            if [ -z "${FFAUDIO_ALLOW_NON_LGPL:-}" ]; then
                echo "This FFmpeg configured with CONFIG_$flag set. Pass --allow-non-lgpl (or set FFAUDIO_ALLOW_NON_LGPL=1) to accept the different redistribution terms; see README.md." >&2
                return 1
            fi

            if [ "$flag" = GPL ]; then
                echo "WARNING: This FFmpeg is GPL, not LGPL. Distribution must comply with the GPL." >&2
            else
                echo "WARNING: This FFmpeg is nonfree. FFmpeg marks the resulting binary as unredistributable." >&2
            fi
        fi
    done
}
