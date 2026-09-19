#!/usr/bin/env bash
# What the static builds are allowed to decode, in one place.
#
# Sourced rather than run. It exists because the phone builds are
# --disable-everything plus an explicit list, and that list was written twice -
# once in ios/build-ffmpeg.sh and once in android/build-ffmpeg.sh - with a
# comment in each saying it must match the other. Two lists that must be equal
# is one list.
#
# There are two of them, and which one a build gets is FFAUDIO_VARIANT:
#
#   slim (the default) - a music library and the containers a server hands over
#     on a stream. This is the list Flower has always built, and it is what
#     keeps an iOS slice under 2MB and an Android ABI under 1.5MB. Anything not
#     named here does not decode, which is a statement rather than an oversight.
#
#   full - every audio decoder FFmpeg has, and every demuxer, at whatever size
#     that costs. What a general-purpose decode library has to be able to offer
#     someone whose files are not a music library: broadcast formats, game
#     audio, speech codecs, the ADPCM family.
#
# `full` is not "drop --disable-everything": that would pull in the whole video
# decoder set for a façade that hands back PCM and cannot express a frame. It
# is instead the audio half of FFmpeg's own decoder list, read out of the
# source tree that is about to be configured - see ffaudio_component_flags.
#
# Neither variant enables anything under a licence this repo cannot ship: no
# --enable-gpl and no --enable-nonfree appear anywhere, and nothing here can
# introduce one, because every name below is a decoder or a demuxer rather than
# a configure switch.

# What a music library is made of, plus the containers a server might hand over
# on a stream.
#
# The dsd_* decoders are here because the dsf demuxer is: a .dsf that demuxes
# and then finds no decoder fails later and worse than one that was never
# claimed at all, and that is exactly what this list did until now. They decode
# DSD to float PCM at an eighth of the DSD rate, which is what the façade
# resamples from - see docs/AUDIOPHILE-PLAN.md in Flower for what DSD support
# is and is not.
ffaudio_slim_decoders="mp3,mp3float,aac,aac_latm,alac,flac,vorbis,opus,wavpack,ape,dsd_lsbf,dsd_msbf,dsd_lsbf_planar,dsd_msbf_planar,pcm_s16le,pcm_s16be,pcm_s24le,pcm_s24be,pcm_s32le,pcm_u8,pcm_f32le,pcm_f64le"
ffaudio_slim_demuxers="mov,mp3,flac,wav,w64,ogg,matroska,aac,ape,wv,aiff,dsf"
ffaudio_slim_parsers="mpegaudio,aac,aac_latm,flac,vorbis,opus"

ffaudio_variant="${FFAUDIO_VARIANT:-slim}"
case "$ffaudio_variant" in
    slim|full) ;;
    *) echo "FFAUDIO_VARIANT must be 'slim' or 'full', not '$ffaudio_variant'." >&2; return 1 2>/dev/null || exit 1 ;;
esac

# Optional FFmpeg configure switches supplied by the caller. This is primarily
# for licensing choices such as --enable-gpl or --enable-nonfree, but accepts
# any whitespace-separated configure flags. Values containing whitespace are
# not supported; put compiler and linker flags in the platform scripts.
ffaudio_extra_configure_flags=()
if [ -n "${FFAUDIO_FFMPEG_CONFIGURE_FLAGS:-}" ]; then
    read -r -a ffaudio_extra_configure_flags <<< "$FFAUDIO_FFMPEG_CONFIGURE_FLAGS"
fi

# Every audio decoder in an unpacked FFmpeg source tree, read out of
# libavcodec/allcodecs.c.
#
# That file groups its declarations by kind under comment markers - video,
# audio, PCM, DPCM, ADPCM, then subtitles - and the four kinds between "audio
# codecs" and "subtitles" are precisely the ones that produce samples. Reading
# them is how `full` stays a statement about audio rather than a promise to
# ship all of FFmpeg.
#
# Deliberately fragile in one direction only: if a future FFmpeg renames or
# drops those markers this fails loudly with an empty list, rather than
# quietly configuring a build with no decoders in it.
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

# The configure arguments for the chosen variant, one per line, given the
# unpacked FFmpeg source directory. Callers expand it into their own configure
# call; nothing here knows about a target.
#
# --enable-protocol=file in both, and nothing else: the façade never lets
# FFmpeg open a URL. A streamed track arrives through the façade's own AVIO
# callbacks over a Stream the caller supplies.
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
        # Every demuxer and every parser, not only the ones that carry audio: a
        # demuxer costs a table and a probe function, and the file a caller
        # hands over is theirs to name rather than ours to predict. configure
        # warns about the handful whose dependencies this build disables
        # (network, external libraries) and drops them, which is the intended
        # outcome and not an error.
        printf '%s\n' \
            "--enable-decoder=$decoders" \
            "--enable-demuxer=*" \
            "--enable-parser=*"
    fi

    printf '%s\n' --enable-protocol=file
}

# Repository builds are LGPL-only by default. A caller may deliberately choose
# another configuration, but must acknowledge the different distribution
# terms with FFAUDIO_ALLOW_NON_LGPL=1. Checking config.h catches both explicit
# flags and licensing changes pulled in by other configure options.
ffaudio_assert_lgpl() {
    local build_dir="$1"
    local header="$build_dir/config.h"
    [ -f "$header" ] || { echo "No config.h under $build_dir - configure did not finish." >&2; return 1; }

    local flag
    for flag in GPL NONFREE; do
        if grep -q "^#define CONFIG_$flag 1\$" "$header"; then
            if [ -z "${FFAUDIO_ALLOW_NON_LGPL:-}" ]; then
                echo "This FFmpeg configured with CONFIG_$flag set. Set FFAUDIO_ALLOW_NON_LGPL=1 to acknowledge the changed redistribution terms; see README.md." >&2
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
