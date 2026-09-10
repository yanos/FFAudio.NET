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

    if(FFAUDIO_STATIC)
        # A shipping desktop native links FFmpeg in, the same way the phones
        # do. Not for size - it costs size - but because a dynamically linked
        # façade records the absolute path of the dylib or .so it was built
        # against, and a package is a binary that will be restored on a
        # machine that has never had that file. See native/host-ffmpeg.sh,
        # which builds the prefix this reads.
        #
        # The static flags rather than the imported target: PkgConfig::FFMPEG
        # carries Libs alone, and what makes a static link resolve is
        # Libs.private - libm, zlib, and whatever else the four archives left
        # undefined.
        target_include_directories(ffaudio PRIVATE ${FFMPEG_STATIC_INCLUDE_DIRS})

        # `-framework CoreVideo` is two words, and pkg_check_modules hands back
        # a list - so the two arrive as separate entries and CMake reads the
        # second as a library name, asking the linker for a `libCoreVideo` that
        # does not exist. Rejoining them into one `-Wl,` flag is what keeps
        # Apple's frameworks (which libavutil's own .pc names) linkable through
        # a mechanism that was designed around `-l`.
        set(ffaudio_static_link "")
        set(ffaudio_pending_framework OFF)
        foreach(flag IN LISTS FFMPEG_STATIC_LDFLAGS)
            if(ffaudio_pending_framework)
                list(APPEND ffaudio_static_link "-Wl,-framework,${flag}")
                set(ffaudio_pending_framework OFF)
            elseif(flag STREQUAL "-framework")
                set(ffaudio_pending_framework ON)
            else()
                list(APPEND ffaudio_static_link "${flag}")
            endif()
        endforeach()
        target_link_libraries(ffaudio PRIVATE ${ffaudio_static_link})
    else()
        target_link_libraries(ffaudio PRIVATE PkgConfig::FFMPEG)
    endif()
endif()

# The sixteen functions and nothing else.
#
# CMAKE_C_VISIBILITY_PRESET above is enough while FFmpeg is a separate library:
# it hides everything ffaudio.c does not export, and avformat's own symbols
# were never in this binary to begin with. Link FFmpeg in and they are, in
# their thousands, none of them compiled with hidden visibility - and a façade
# that re-exports avcodec_send_packet is precisely the second route to FFmpeg
# this eight-function surface exists in order not to have. A visibility preset
# cannot reach inside an archive; only the linker can.
#
# The phones already do this in their own scripts (ios/build.sh's
# -exported_symbols_list, android/build.sh's version script). This is the same
# narrowing for the two platforms that go through CMake, derived the same way -
# from the header's own FFAUDIO_API lines, so the ABI has one definition rather
# than four.
if(FFAUDIO_STATIC AND NOT MSVC)
    file(STRINGS "${CMAKE_CURRENT_SOURCE_DIR}/ffaudio.h" ffaudio_api_lines REGEX "FFAUDIO_API")
    set(ffaudio_exports "")
    foreach(line IN LISTS ffaudio_api_lines)
        # The name immediately before the parameter list. FFAUDIO_API is on the
        # functions and on nothing else, which is what makes the header its own
        # export list; the two lines that define the macro name no function and
        # so contribute nothing.
        if(line MATCHES "[ *](ffaudio_[a-z0-9_]+)\\(")
            list(APPEND ffaudio_exports "${CMAKE_MATCH_1}")
        endif()
    endforeach()
    list(REMOVE_DUPLICATES ffaudio_exports)
    list(LENGTH ffaudio_exports ffaudio_export_count)

    # Fragile in one direction only. An empty list here would link a façade
    # exporting nothing, which fails at the first P/Invoke on a consumer's
    # machine rather than here.
    if(ffaudio_export_count EQUAL 0)
        message(FATAL_ERROR "Found no FFAUDIO_API functions in ffaudio.h - its export macro must have changed")
    endif()
    message(STATUS "ffaudio: exporting ${ffaudio_export_count} symbols")

    if(APPLE)
        # Mach-O wants the assembler's names, which carry a leading underscore.
        set(ffaudio_symbol_file "${CMAKE_CURRENT_BINARY_DIR}/exported_symbols.txt")
        set(ffaudio_symbol_text "")
        foreach(symbol IN LISTS ffaudio_exports)
            string(APPEND ffaudio_symbol_text "_${symbol}\n")
        endforeach()
        file(WRITE "${ffaudio_symbol_file}" "${ffaudio_symbol_text}")
        target_link_options(ffaudio PRIVATE "-Wl,-exported_symbols_list,${ffaudio_symbol_file}")
    else()
        set(ffaudio_symbol_file "${CMAKE_CURRENT_BINARY_DIR}/ffaudio.map")
        set(ffaudio_symbol_text "FFAUDIO_1 {\n  global:\n")
        foreach(symbol IN LISTS ffaudio_exports)
            string(APPEND ffaudio_symbol_text "    ${symbol};\n")
        endforeach()
        string(APPEND ffaudio_symbol_text "  local: *;\n};\n")
        file(WRITE "${ffaudio_symbol_file}" "${ffaudio_symbol_text}")
        target_link_options(ffaudio PRIVATE "-Wl,--version-script,${ffaudio_symbol_file}")
    endif()

    set_property(TARGET ffaudio APPEND PROPERTY LINK_DEPENDS "${ffaudio_symbol_file}")
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
