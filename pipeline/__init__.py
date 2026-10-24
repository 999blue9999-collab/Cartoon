"""Episode-animatic pipeline for the GOLU - Kal Ka Dost series.

A reusable, config-driven tool that turns an external scene list
(keyframes + Hindi dialogue clips + camera moves + timing) into an
episode-length animatic MP4, a synchronized WAV mix, Devanagari
subtitles (sidecar .srt and optional burned-in), and a notes file.

This is an animatic/previs tool: mouth movement is an amplitude-driven
approximation (not neural lip-sync), and dialogue audio is scratch TTS
(not a final dub).
"""

__version__ = "0.1.0"
