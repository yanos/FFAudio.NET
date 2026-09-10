cmake_minimum_required(VERSION 3.20)
project(ffaudio C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_C_VISIBILITY_PRESET hidden)

# The façade is one translation unit on purpose - see ffaudio.h for why
# the surface is this small.
add_library(ffaudio SHARED ffaudio.c)
target_include_directories(ffaudio PRIVATE ${CMAKE_CURRENT_SOURCE_DIR})

if(MSVC)
    # Windows is the one platform with no pkg-config to ask and no FFmpeg to
    # find - no distro package, no MacPorts, nothing on a default path - so it
    # is handed a prefix instead: native/ffmpeg/windows/build.ps1 unpacks an
    # LGPL shared FFmpeg build and points FFAUDIO_PREFIX at it. The
    # layout is that build's own: include/ for the headers, lib/ for the MSVC
    # import libraries, bin/ for the DLLs the façade will need beside it at
    # run time.
    if(NOT FFAUDIO_PREFIX)
        message(FATAL_ERROR "FFAUDIO_PREFIX must name an FFmpeg prefix (include/ + lib/) - run native/ffmpeg/windows/build.ps1 rather than cmake directly")
    endif()

    target_include_directories(ffaudio PRIVATE "${FFAUDIO_PREFIX}/include")

    # The four the façade actually calls into. avdevice, avfilter and swscale
    # are in that prefix too and are deliberately not linked: an unused import
    # is a DLL that would then have to be shipped and kept replaceable.
    foreach(component avformat avcodec avutil swresample)
        find_library(FFMPEG_${component}_LIBRARY
            NAMES ${component}
            PATHS "${FFAUDIO_PREFIX}/lib"
            NO_DEFAULT_PATH
            REQUIRED)
        target_link_libraries(ffaudio PRIVATE "${FFMPEG_${component}_LIBRARY}")
    endforeach()
else()
    # pkg-config is how every other FFmpeg build - MacPorts, a distro, or one
    # configured and installed by native/ffmpeg/<platform>/build.sh - describes
    # itself. The cross builds point PKG_CONFIG_LIBDIR at their own prefix
    # rather than teaching CMake a second way to find FFmpeg.
    find_package(PkgConfig REQUIRED)
    # The floor is FFmpeg 5.1, which is where AVChannelLayout and
    # swr_alloc_set_opts2 arrived - the newest APIs this façade uses, and the ones
    # that make its channel handling a single code path. Higher numbers were an
    # accident of the machine it was first built on rather than a requirement, and
    # an accident that costs something real: Ubuntu 24.04 ships FFmpeg 6, so a
    # floor of 61 meant the Linux build could not find a distro FFmpeg at all.
    pkg_check_modules(FFMPEG REQUIRED IMPORTED_TARGET
        libavformat>=59.27
        libavcodec>=59.37
        libavutil>=57.28
        libswresample>=4.7)

    target_link_libraries(ffaudio PRIVATE PkgConfig::FFMPEG)
endif()

if(FFAUDIO_STATIC)
    # Mobile links FFmpeg in rather than shipping four more libraries per ABI,
    # so the façade is the only binary a consumer has to package there.
    target_link_libraries(ffaudio PRIVATE ${FFMPEG_STATIC_LDFLAGS})
endif()

if(APPLE)
    set_target_properties(ffaudio PROPERTIES
        INSTALL_NAME_DIR "@rpath"
        BUILD_WITH_INSTALL_NAME_DIR TRUE)
endif()

if(FFAUDIO_ASAN)
    target_compile_options(ffaudio PRIVATE -fsanitize=address -fno-omit-frame-pointer -g)
    target_link_options(ffaudio PRIVATE -fsanitize=address)
endif()
