# Native safety regressions

Run from the repository root with FFmpeg development libraries available to
`pkg-config` (Clang or GCC for the sanitizer flags):

```sh
cmake -S native -B native/safety/build -DCMAKE_BUILD_TYPE=Debug \
    -DFFAUDIO_BUILD_TESTS=ON -DFFAUDIO_ASAN=ON -DFFAUDIO_UBSAN=ON
cmake --build native/safety/build --parallel
ctest --test-dir native/safety/build --output-on-failure
```

Each CTest case runs in a separate process with a 15-second timeout, so a
native crash or parser hang fails the test without hanging the test runner.
UBSan aborts on undefined arithmetic instead of only printing a warning.

The tests cover allocation overflow, malformed and truncated picture headers,
changes to frame format/rate/layout, and decoding an MP3 that changes from
stereo to mono. The MP3 regression checks all four output sample formats,
resampling, path and custom IO input, repeated errors, and seeking after the
error. The cover-art regression opens and reads an MP3 containing the JPEG
bytes that previously caused an infinite loop.

The fixtures contain generated sine waves, with no third-party recordings.
Regenerate them with `python3 tests/native/generate_fixtures.py`; this needs
the FFmpeg CLI with the `libmp3lame` encoder. Normal tests use the checked-in
files and need no encoder or Python. The safety executable includes the
facade's C source to reach private helpers without exporting a testing ABI.
