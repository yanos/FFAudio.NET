cmake_minimum_required(VERSION 3.20)
project(ffaudio C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_C_VISIBILITY_PRESET hidden)

# Keep the stable façade in one translation unit.
add_library(ffaudio SHARED ffaudio.c)
target_include_directories(ffaudio PRIVATE ${CMAKE_CURRENT_SOURCE_DIR})

if(MSVC)
    # Windows receives an explicit FFmpeg prefix instead of using pkg-config.
    if(NOT FFAUDIO_PREFIX)
        message(FATAL_ERROR "FFAUDIO_PREFIX must name an FFmpeg prefix (include/, and lib/ or bin/) - run native/windows/build.ps1 rather than cmake directly")
    endif()

    target_include_directories(ffaudio PRIVATE "${FFAUDIO_PREFIX}/include")

    # Link only the four FFmpeg libraries used by the façade. An MSVC-built
    # FFmpeg installs its import libraries beside the DLLs in bin/; other
    # distributions put them in lib/.
    foreach(component avformat avcodec avutil swresample)
        find_library(FFMPEG_${component}_LIBRARY
            NAMES ${component}
            PATHS "${FFAUDIO_PREFIX}/lib" "${FFAUDIO_PREFIX}/bin"
            NO_DEFAULT_PATH
            REQUIRED)
        target_link_libraries(ffaudio PRIVATE "${FFMPEG_${component}_LIBRARY}")
    endforeach()
else()
    # Non-Windows builds describe FFmpeg through pkg-config.
    find_package(PkgConfig REQUIRED)
    # FFmpeg 5.1 introduced AVChannelLayout and swr_alloc_set_opts2.
    pkg_check_modules(FFMPEG REQUIRED IMPORTED_TARGET
        libavformat>=59.27
        libavcodec>=59.37
        libavutil>=57.28
        libswresample>=4.7)

    if(FFAUDIO_STATIC)
        # Use static pkg-config flags, including Libs.private dependencies.
        target_include_directories(ffaudio PRIVATE ${FFMPEG_STATIC_INCLUDE_DIRS})

        # Rejoin split `-framework Name` entries into one linker option.
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

# Static archives ignore target visibility, so derive a linker export list
# from FFAUDIO_API declarations and keep FFmpeg's symbols private.
if(FFAUDIO_STATIC AND NOT MSVC)
    file(STRINGS "${CMAKE_CURRENT_SOURCE_DIR}/ffaudio.h" ffaudio_api_lines REGEX "FFAUDIO_API")
    set(ffaudio_exports "")
    foreach(line IN LISTS ffaudio_api_lines)
        # Capture the function name immediately before its parameter list.
        if(line MATCHES "[ *](ffaudio_[a-z0-9_]+)\\(")
            list(APPEND ffaudio_exports "${CMAKE_MATCH_1}")
        endif()
    endforeach()
    list(REMOVE_DUPLICATES ffaudio_exports)
    list(LENGTH ffaudio_exports ffaudio_export_count)

    # Fail here instead of producing a library with no public entry points.
    if(ffaudio_export_count EQUAL 0)
        message(FATAL_ERROR "Found no FFAUDIO_API functions in ffaudio.h - its export macro must have changed")
    endif()
    message(STATUS "ffaudio: exporting ${ffaudio_export_count} symbols")

    if(APPLE)
        # Mach-O export names include a leading underscore.
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

option(FFAUDIO_BUILD_TESTS "Build native safety regression tests" OFF)

function(ffaudio_sanitizers target)
    if(FFAUDIO_ASAN)
        target_compile_options(${target} PRIVATE -fsanitize=address -fno-omit-frame-pointer -g)
        target_link_options(${target} PRIVATE -fsanitize=address)
    endif()
    if(FFAUDIO_UBSAN)
        target_compile_options(${target} PRIVATE -fsanitize=undefined -fno-sanitize-recover=undefined -g)
        target_link_options(${target} PRIVATE -fsanitize=undefined)
    endif()
endfunction()

ffaudio_sanitizers(ffaudio)

if(FFAUDIO_BUILD_TESTS)
    enable_testing()
    add_executable(ffaudio_safety_tests ../tests/native/safety_tests.c)
    # The test includes the facade to reach private helpers. Reuse its FFmpeg
    # dependencies, without its shipping export restrictions or shared library.
    target_include_directories(ffaudio_safety_tests PRIVATE $<TARGET_PROPERTY:ffaudio,INCLUDE_DIRECTORIES>)
    target_link_libraries(ffaudio_safety_tests PRIVATE $<TARGET_PROPERTY:ffaudio,LINK_LIBRARIES>)
    ffaudio_sanitizers(ffaudio_safety_tests)
    set(ffaudio_fixtures "${CMAKE_CURRENT_SOURCE_DIR}/../tests/native/fixtures")
    add_test(NAME native_safety COMMAND ffaudio_safety_tests)
    add_test(NAME changing_input COMMAND ffaudio_safety_tests changed-file "${ffaudio_fixtures}/changing-channels.mp3")
    add_test(NAME jpeg_cover COMMAND ffaudio_safety_tests cover-file "${ffaudio_fixtures}/jpeg-cover-hang.mp3")
    set_tests_properties(native_safety changing_input jpeg_cover PROPERTIES TIMEOUT 15)
endif()
