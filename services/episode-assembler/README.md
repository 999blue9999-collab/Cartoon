# Episode Assembler Service

Small FastAPI service called by the n8n **Season Producer** workflow (`Assemble Episode` node) to master a finished episode:

- downloads scene videos (synced lipsync output)
- normalizes each to 1920×1080 @ 24fps (letterboxed)
- concatenates scenes into one episode
- burns dialogue subtitles (SRT, equal split per scene)
- mixes voiceover with the season music track (music ducked to ~15%)
- writes the MP4 master to `./output`

## Run locally

```bash
cd services/episode-assembler
pip install -r requirements.txt
# ffmpeg must be on PATH (brew install ffmpeg / apt-get install ffmpeg)
FFMPEG_ENABLED=true PUBLIC_BASE_URL=http://localhost:8000 uvicorn app:app --reload
```

## Run with Docker

```bash
docker build -t cartoon-assembler services/episode-assembler
docker run -p 8000:8000 -e PUBLIC_BASE_URL=http://localhost:8000 cartoon-assembler
```

## n8n wiring

In the Season Producer `Config` node set `ASSEMBLER_URL` to this service's base URL
(e.g. `http://episode-assembler:8000` inside docker compose).

Without ffmpeg the service writes a manifest JSON and returns `status: ffmpeg_unavailable`,
so the n8n wiring can be tested end-to-end without rendering real video.
