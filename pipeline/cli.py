"""Command-line interface: ``python -m pipeline build ...``."""
from __future__ import annotations

import argparse
import sys

from . import builder
from . import config as config_mod


def _cmd_build(args) -> int:
    if args.validate_only:
        episode = config_mod.load_config(args.config)
        if args.fps_unique is not None:
            episode.fps_unique = args.fps_unique
        if args.font is not None:
            episode.font_path = args.font
        if args.burn_subtitles is not None:
            episode.burn_subtitles = args.burn_subtitles
        config_mod.validate(episode, args.assets_dir, probe_durations=True)
        total = episode.total_duration or episode.shots_duration
        print(
            f"OK: {episode.title!r} validates - {len(episode.shots)} shots / "
            f"{total:.1f}s ({episode.width}x{episode.height}, "
            f"{episode.fps_unique}fps unique -> {episode.fps_delivery}fps delivery)."
        )
        return 0

    if not args.out_dir:
        print("error: --out-dir is required unless --validate-only", file=sys.stderr)
        return 2

    def progress(fi, n):
        print(f"  frame {fi}/{n}")

    result = builder.build(
        args.config,
        args.assets_dir,
        args.out_dir,
        font_path=args.font,
        burn_subtitles=args.burn_subtitles,
        fps_unique=args.fps_unique,
        progress=progress,
    )
    print(
        f"OK {result.mp4_path} | {result.duration:.1f}s | "
        f"{result.n_frames} drawings | wav={result.wav_path} | "
        f"srt={result.srt_path} | notes={result.notes_path}"
    )
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="python -m pipeline",
        description="Config-driven episode-animatic builder.",
    )
    sub = parser.add_subparsers(dest="command", required=True)

    b = sub.add_parser("build", help="Validate and render an episode animatic.")
    b.add_argument("--config", required=True, help="Path to the YAML/JSON episode config.")
    b.add_argument(
        "--assets-dir", required=True,
        help="Directory that keyframe/audio paths in the config resolve against.",
    )
    b.add_argument("--out-dir", help="Output directory for the mp4/wav/srt/notes.")
    b.add_argument(
        "--font",
        help="Path to a Devanagari-capable TrueType font, used when burning in "
        "subtitles. Overrides the episode 'font_path'.",
    )
    b.add_argument(
        "--burn-subtitles", dest="burn_subtitles", action="store_true",
        default=None,
        help="Burn the Devanagari dialogue text into the frames (lower-third "
        "caption). Requires a Devanagari font via --font or episode font_path.",
    )
    b.add_argument(
        "--no-burn-subtitles", dest="burn_subtitles", action="store_false",
        help="Disable burned-in subtitles even if the config sets "
        "burn_subtitles: true. The sidecar .srt is still written.",
    )
    b.add_argument("--fps-unique", type=int, help="Override unique drawings per second.")
    b.add_argument(
        "--validate-only", action="store_true",
        help="Validate the config and print a summary without rendering.",
    )
    b.set_defaults(func=_cmd_build)
    return parser


def main(argv=None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    try:
        return args.func(args)
    except config_mod.ConfigError as exc:
        print(f"config error: {exc}", file=sys.stderr)
        return 1
    except Exception as exc:  # noqa: BLE001 - surface a clean message, non-zero exit
        print(f"error: {exc}", file=sys.stderr)
        return 1
