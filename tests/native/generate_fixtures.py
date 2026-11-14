"""Regenerate synthetic regression fixtures; requires the ffmpeg CLI with libmp3lame."""

from pathlib import Path
import subprocess
import tempfile


def generate():
    destination = Path(__file__).parent / "fixtures"
    destination.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="ffaudio-fixtures-") as temporary:
        tracks = []
        for channels in (2, 1):
            output = Path(temporary) / f"{channels}ch.mp3"
            subprocess.run(
                [
                    "ffmpeg", "-v", "error", "-f", "lavfi", "-i",
                    "sine=frequency=440:duration=2:sample_rate=44100",
                    "-ac", str(channels), "-c:a", "libmp3lame",
                    "-write_xing", "0", "-id3v2_version", "0", str(output),
                ],
                check=True,
            )
            tracks.append(output.read_bytes())

    (destination / "changing-channels.mp3").write_bytes(tracks[0] + tracks[1])

    # ID3v2.3 APIC holding the six bytes that formerly stalled jpeg_size.
    content = b"\x00image/jpeg\x00\x03\x00\xff\xd8\xff\xff\x00\x00"
    frame = b"APIC" + len(content).to_bytes(4, "big") + b"\x00\x00" + content
    size = len(frame)
    syncsafe = bytes((size >> shift) & 127 for shift in (21, 14, 7, 0))
    tag = b"ID3\x03\x00\x00" + syncsafe + frame
    (destination / "jpeg-cover-hang.mp3").write_bytes(tag + tracks[0])


if __name__ == "__main__":
    generate()
