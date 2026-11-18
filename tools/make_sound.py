"""Writes assets/warning.wav, the chime Linux builds play when the final
warning starts (Windows uses the system's own warning sound).

Run: python3 tools/make_sound.py
"""
import math
import struct
import wave
from pathlib import Path

RATE = 44100


def tone(freq, seconds, volume=0.45):
    n = int(RATE * seconds)
    for i in range(n):
        t = i / RATE
        envelope = min(1.0, i / (RATE * 0.01)) * math.exp(-4.0 * t)   # quick attack, gentle decay
        yield volume * envelope * math.sin(2 * math.pi * freq * t)


samples = list(tone(880, 0.22)) + [0.0] * int(RATE * 0.06) + list(tone(660, 0.35))
out = Path(__file__).resolve().parent.parent / "assets" / "warning.wav"
with wave.open(str(out), "wb") as w:
    w.setnchannels(1)
    w.setsampwidth(2)
    w.setframerate(RATE)
    w.writeframes(b"".join(struct.pack("<h", int(s * 32767)) for s in samples))
print(f"wrote {out}")
