#!/usr/bin/env bash
# Command-line switches for the build scripts, in one place so every script
# spells them the same way and --help lists exactly the ones it takes.
#
# Sourced rather than run, like codec-set.sh. Each switch sets the environment
# variable that already meant the same thing, and exports it. The scripts call
# one another - build-all.sh runs macos/build.sh, which runs host-ffmpeg.sh,
# which sources codec-set.sh - and an exported variable reaches every level
# without each one parsing and forwarding the switch again. The variables still
# work on their own, which is what CI's env: blocks rely on.
#
# What the switches add over the variables is that a typo fails. FFAUDIO_STATC=1
# is ignored without a word and builds the development binary, linked to this
# machine's FFmpeg - the one that fails on everybody else's. --statc stops.
#
# A script sets these before calling ffaudio_parse_options "$@":
#
#   ffaudio_usage     its synopsis, e.g. "native/ios/build.sh [options]"
#   ffaudio_about     a paragraph on what it does
#   ffaudio_options   the switch names it accepts, space-separated
#   ffaudio_operands  what its positional arguments are, for --help; empty if
#                     it takes none, in which case one is an error
#
# and afterwards reads its positional arguments - and anything after a bare
# `--` - out of ffaudio_args.
#
# Bash 3.2 compatible, because that is the bash a Mac has: no associative
# arrays, so the switch table is a pair of case statements.

# The environment variable a switch sets.
_ffaudio_option_var() {
    case "$1" in
        static)                  echo FFAUDIO_STATIC ;;
        variant)                 echo FFAUDIO_VARIANT ;;
        rebuild-ffmpeg)          echo FFAUDIO_REBUILD_FFMPEG ;;
        ffmpeg-version)          echo FFAUDIO_FFMPEG_VERSION ;;
        configure-flags)         echo FFAUDIO_FFMPEG_CONFIGURE_FLAGS ;;
        allow-non-lgpl)          echo FFAUDIO_ALLOW_NON_LGPL ;;
        archs)                   echo FFAUDIO_ARCHS ;;
        macos-deployment-target) echo MACOSX_DEPLOYMENT_TARGET ;;
        ndk)                     echo ANDROID_NDK_HOME ;;
        *) return 1 ;;
    esac
}

# The value's placeholder in --help, or nothing for an on/off switch.
_ffaudio_option_value() {
    case "$1" in
        variant)                 echo "slim|full" ;;
        ffmpeg-version)          echo "VERSION" ;;
        configure-flags)         echo "FLAGS" ;;
        archs)                   echo "ARCHS" ;;
        macos-deployment-target) echo "VERSION" ;;
        ndk)                     echo "PATH" ;;
        *) echo "" ;;
    esac
}

_ffaudio_option_help() {
    case "$1" in
        static)
            echo "Build a static LGPL FFmpeg from source and link it in: the"
            echo "portable build that gets packaged. Without it, macos and linux"
            echo "link this machine's FFmpeg, which is for development only." ;;
        variant)
            echo "Which decoders the FFmpeg built from source gets. slim (the"
            echo "default) is a music library; full is every audio decoder." ;;
        rebuild-ffmpeg)
            echo "Rebuild FFmpeg even though a build of it already exists. Needed"
            echo "after changing --configure-flags, which are not part of the"
            echo "build's folder name." ;;
        ffmpeg-version)
            echo "The FFmpeg release to build from source (default 9.0.2). Each"
            echo "version is kept in its own folder, so switching is a relink"
            echo "once it has been built. Windows always uses its pinned download." ;;
        configure-flags)
            echo "Extra FFmpeg configure flags, space-separated, e.g."
            echo "--configure-flags=\"--enable-gpl\"." ;;
        allow-non-lgpl)
            echo "Allow an FFmpeg that configures as GPL or nonfree. Without it,"
            echo "such a build stops. See the licensing section of README.md." ;;
        archs)
            echo "macOS architectures for a development build (default arm64)."
            echo "A --static build is always the host's own architecture." ;;
        macos-deployment-target)
            echo "The oldest macOS a static build runs on (default 11.0)." ;;
        ndk)
            echo "The Android NDK to build with." ;;
    esac
}

ffaudio_print_help() {
    echo "Usage: $ffaudio_usage"
    echo
    echo "$ffaudio_about"
    if [ -n "${ffaudio_operands:-}" ]; then
        echo
        echo "$ffaudio_operands"
    fi
    echo
    echo "Options:"

    local name value var
    for name in $ffaudio_options; do
        value="$(_ffaudio_option_value "$name")"
        var="$(_ffaudio_option_var "$name")"
        echo "  --$name${value:+ $value}"
        _ffaudio_option_help "$name" | sed 's/^/      /'
        echo "      [$var=${value:-1}]"
    done
    echo "  -h, --help"
    echo "      Show this help."
    echo
    echo "Each option can also be set with the environment variable shown in"
    echo "brackets, which is how it reaches the scripts this one runs."
}

_ffaudio_option_error() {
    echo "$1" >&2
    echo "Run '$ffaudio_usage_command --help' for the options." >&2
    exit 2
}

ffaudio_parse_options() {
    ffaudio_args=()
    ffaudio_usage_command="${ffaudio_usage%% *}"

    local arg name value has_value var
    while [ $# -gt 0 ]; do
        arg="$1"
        has_value=
        case "$arg" in
            -h|--help)
                ffaudio_print_help
                exit 0
                ;;
            --)
                shift
                ffaudio_args+=("$@")
                break
                ;;
            --*=*)
                name="${arg%%=*}"
                name="${name#--}"
                value="${arg#*=}"
                has_value=1
                ;;
            --*)
                name="${arg#--}"
                ;;
            -*)
                _ffaudio_option_error "Unknown option '$arg'."
                ;;
            *)
                ffaudio_args+=("$arg")
                shift
                continue
                ;;
        esac

        case " $ffaudio_options " in
            *" $name "*) ;;
            *) _ffaudio_option_error "Unknown option '--$name'." ;;
        esac
        var="$(_ffaudio_option_var "$name")"

        if [ -n "$(_ffaudio_option_value "$name")" ]; then
            if [ -z "$has_value" ]; then
                [ $# -ge 2 ] || _ffaudio_option_error "--$name needs a value."
                value="$2"
                shift
            fi
            [ -n "$value" ] || _ffaudio_option_error "--$name needs a value."
        else
            [ -z "$has_value" ] || _ffaudio_option_error "--$name takes no value."
            value=1
        fi

        export "$var=$value"
        shift
    done

    if [ -z "${ffaudio_operands:-}" ] && [ ${#ffaudio_args[@]} -gt 0 ]; then
        _ffaudio_option_error "Unexpected argument '${ffaudio_args[0]}'."
    fi
}
