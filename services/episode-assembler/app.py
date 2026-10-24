"""Episode assembler for the Cartoon Studio n8n pipeline.

Called by n8n (Season Producer) at POST /assemble-episode with a JSON payload:

{
  "episode_key": "S1E1",
  "season": "1",
  "episode": "1",
  "title": "The Missing Gadget",
  "music_url": "https://.../theme.mp3",
  "target_duration": 600,
  "scenes": [
    {"scene_no": 1, "video_url": "...", "audio_url": "...", "dialogue_line": "...", "duration_seconds": 6},
    ...
  ]
}

It normalizes every scene to 1080p24, concatenates them, mixes voiceover with the
season music track, burns dialogue subtitles, and returns the mastered MP4.

Without ffmpeg (or with FFMPEG_ENABLED=false) it writes a manifest JSON instead,
so the n8n wiring can be dry-tested in a sandbox.
"""

import json
import os
import subprocess
import tempfile
import uuid
from pathlib import Path
from typing import Any, Dict, List, Optional

import httpx
from fastapi import FastAPI, HTTPException
from fastapi.responses import FileResponse, JSONResponse
from pydantic import BaseModel, Field

app = FastAPI(title="Cartoon Studio Episode Assembler")

OUTPUT_DIR = Path(os.getenv("OUTPUT_DIR", "./output"))
OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
PUBLIC_BASE_URL = os.getenv("PUBLIC_BASE_URL", "")
FFMPEG_ENABLED = os.getenv("FFMPEG_ENABLED", "true").lower() == "true"
FFMPEG_BIN = os.getenv("FFMPEG_BIN", "ffmpeg")
FFPROBE_BIN = os.getenv("FFPROBE_BIN", "ffprobe")
WORK_DIR = Path(os.getenv("WORK_DIR", "./work"))
WORK_DIR.mkdir(parents=True, exist_ok=True)


class Scene(BaseModel):
    scene_no: int
    video_url: Optional[str] = None
    audio_url: Optional[str] = None
    dialogue_line: str = ""
    duration_seconds: float = 6.0


class AssembleRequest(BaseModel):
    episode_key: str
    season: str = ""
    episode: str = ""
    title: str = ""
    music_url: Optional[str] = None
    target_duration: float = 600.0
    scenes: List[Scene] = []
    created_at: Optional[str] = None


async def download_file(client: httpx.AsyncClient, url: str, dest: Path) -> None:
    async with client.stream("GET", url) as resp:
        resp.raise_for_status()
        with open(dest, "wb") as f:
            async for chunk in resp.aiter_bytes():
                f.write(chunk)


def probe_duration(path: Path) -> float:
    try:
        out = subprocess.run(
            [FFPROBE_BIN, "-v", "error", "-show_entries", "format=duration",
             "-of", "default=noprint_wrappers=1:nokey=1", str(path)],
            capture_output=True, text=True, check=True,
        )
        return float(out.stdout.strip())
    except Exception:
        return 0.0


def normalize_scene(src: Path, dest: Path) -> None:
    """Letterbox/pad to 1920x1080 @ 24fps, keep stereo audio."""
    subprocess.run(
        [
            FFMPEG_BIN, "-y", "-i", str(src),
            "-vf", "scale=1920:1080:force_original_aspect_ratio=decrease,"
                   "pad=1920:1080:(ow-iw)/2:(oh-ih)/2,fps=24",
            "-c:a", "copy", "-movflags", "+faststart",
            str(dest),
        ],
        check=True,
        capture_output=True,
    )


def assemble_episode(req: AssembleRequest) -> Dict[str, Any]:
    if not FFMPEG_ENABLED or subprocess.run(
        ["bash", "-c", f"command -v {FFMPEG_BIN}"],
        capture_output=True,
    ).returncode != 0:
        return {
            "episode_key": req.episode_key,
            "status": "ffmpeg_unavailable",
            "message": "ffmpeg not found or FFMPEG_ENABLED=false; wrote manifest only.",
            "manifest": req.model_dump(),
            "manifest_path": str(OUTPUT_DIR / f"{req.episode_key}.manifest.json"),
        }

    with tempfile.TemporaryDirectory() as tmp:
        tmpdir = Path(tmp)
        normed: List[Path] = []
        durations: List[float] = []

        for idx, scene in enumerate(req.scenes):
            if not scene.video_url:
                continue
            src = tmpdir / f"scene_{idx}.mp4"
            out = tmpdir / f"scene_{idx}_norm.mp4"
            # httpx is sync-incompatible here; use subprocess curl fallback? Use httpx sync client.
            with httpx.Client(timeout=120) as client:
                with open(src, "wb") as f:
                    with client.stream("GET", scene.video_url) as resp:
                        resp.raise_for_status()
                        for chunk in resp.iter_bytes():
                            f.write(chunk)
            normalize_scene(src, out)
            normed.append(out)
            durations.append(probe_duration(out) or scene.duration_seconds or 6.0)

        if not normed:
            raise RuntimeError("no scene videos to assemble")

        # Concat demuxer
        list_file = tmpdir / "concat.txt"
        with open(list_file, "w") as f:
            for p in normed:
                f.write(f"file '{p}'\n")
        raw = tmpdir / "raw.mp4"
        subprocess.run(
            [FFMPEG_BIN, "-y", "-f", "concat", "-safe", "0", "-i", str(list_file),
             "-c", "copy", str(raw)],
            check=True, capture_output=True,
        )

        # Subtitles: equal split of cumulative duration across dialogue lines
        total = sum(durations)
        srt_path = tmpdir / "subs.srt"
        t = 0.0
        with open(srt_path, "w") as f:
            for i, (scene, dur) in enumerate(zip(req.scenes, durations), 1):
                if not scene.video_url:
                    continue
                start, end = t, t + dur
                f.write(f"{i}\n{format_ts(start)} --> {format_ts(end)}\n{scene.dialogue_line}\n\n")
                t = end

        # Mix: episode audio + music (looped, ducked)
        music_path: Optional[Path] = None
        if req.music_url:
            music_path = tmpdir / "music.mp3"
            with httpx.Client(timeout=180) as client:
                with open(music_path, "wb") as f:
                    with client.stream("GET", req.music_url) as resp:
                        resp.raise_for_status()
                        for chunk in resp.iter_bytes():
                            f.write(chunk)

        out_mp4 = OUTPUT_DIR / f"{req.episode_key}.mp4"
        if music_path:
            subprocess.run(
                [
                    FFMPEG_BIN, "-y", "-i", str(raw), "-i", str(music_path),
                    "-filter_complex",
                    "[1:a]aloop=loop=-1:size=2e+09[music];"
                    "[0:a][music]amix=inputs=2:duration=longest:weights=1 0.15[a]",
                    "-map", "0:v", "-map", "[a]",
                    "-c:v", "copy", "-c:a", "aac", "-b:a", "128k",
                    "-vf", f"subtitles='{srt_path}'",
                    str(out_mp4),
                ],
                check=True, capture_output=True,
            )
        else:
            subprocess.run(
                [FFMPEG_BIN, "-y", "-i", str(raw),
                 "-c", "copy", "-vf", f"subtitles='{srt_path}'", str(out_mp4)],
                check=True, capture_output=True,
            )

    episode_url = f"{PUBLIC_BASE_URL}/output/{req.episode_key}.mp4" if PUBLIC_BASE_URL else ""
    return {
        "episode_key": req.episode_key,
        "status": "assembled",
        "episode_url": episode_url,
        "local_path": str(out_mp4),
        "duration_seconds": total,
        "scenes": len(normed),
    }


def format_ts(seconds: float) -> str:
    ms = int(round(seconds * 1000))
    h = ms // 3_600_000
    ms %= 3_600_000
    m = ms // 60_000
    ms %= 60_000
    s = ms // 1000
    ms %= 1000
    return f"{h:02d}:{m:02d}:{s:02d},{ms:03d}"


@app.get("/health")
def health():
    return {"status": "ok", "ffmpeg": FFMPEG_ENABLED}


@app.post("/assemble-episode")
def assemble(req: AssembleRequest):
    try:
        result = assemble_episode(req)
    except Exception as exc:  # noqa: BLE001 - surface error to n8n
        raise HTTPException(status_code=500, detail=str(exc))
    if result.get("status") == "ffmpeg_unavailable":
        manifest_path = OUTPUT_DIR / f"{req.episode_key}.manifest.json"
        manifest_path.write_text(json.dumps(result["manifest"], indent=2))
        return JSONResponse(result)
    if result.get("episode_url") or result.get("local_path"):
        return result
    raise HTTPException(status_code=500, detail="assembly produced no output")


@app.get("/output/{filename}")
def get_output(filename: str):
    path = OUTPUT_DIR / filename
    if not path.exists():
        raise HTTPException(status_code=404, detail="not found")
    return FileResponse(path, media_type="video/mp4")
