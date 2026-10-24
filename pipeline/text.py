"""Devanagari font resolution + burned-in caption rendering.

Burned-in subtitles are an optional, best-effort visual aid on top of the
authoritative sidecar ``.srt``. This module keeps two concerns separate:

* :func:`resolve_devanagari_font` resolves a font path (CLI/config/env) and
  loads a TrueType font *only* after verifying it can actually render
  Devanagari. If no font is supplied, or the supplied font cannot render the
  script, it raises :class:`FontError` with a clear, actionable message. It
  never silently falls back to the PIL default bitmap font (which would render
  tofu boxes), and it never renders tofu.

* :func:`draw_caption` burns a wrapped, centred lower-third caption with a
  semi-transparent band and an outline for legibility.

KNOWN CAVEAT: Pillow does not perform complex-script (HarfBuzz) shaping, so
conjuncts and matra reordering may be imperfect in the burned-in frames. The
sidecar ``.srt`` carries the correct Unicode text and is authoritative.
"""
from __future__ import annotations

import os

from PIL import Image, ImageDraw, ImageFont

# U+0915 DEVANAGARI LETTER KA - a basic consonant every Devanagari font covers.
_DEVANAGARI_PROBE = "\u0915"

NOTO_URL = (
    "https://github.com/notofonts/notofonts.github.io/raw/main/fonts/"
    "NotoSansDevanagari/hinted/ttf/NotoSansDevanagari-Regular.ttf"
)

_FONT_HELP = (
    "Burned-in Devanagari subtitles require a Devanagari-capable TrueType font. "
    "Pass one with --font /path/to/NotoSansDevanagari-Regular.ttf (or set "
    "'font_path' in the episode config). Download Noto Sans Devanagari from:\n"
    f"    {NOTO_URL}"
)


class FontError(RuntimeError):
    """Raised when a usable Devanagari font cannot be resolved or loaded."""


def resolve_font_path(
    cli_font: str | None,
    config_font: str | None,
    *,
    env: dict | None = None,
) -> str | None:
    """Resolve the font path from CLI, then config, then the ANIMATIC_FONT env.

    Returns the first non-empty candidate, or ``None`` when nothing is set.
    Precedence: explicit ``--font`` > episode ``font_path`` > ``ANIMATIC_FONT``.
    """
    env = os.environ if env is None else env
    for candidate in (cli_font, config_font, env.get("ANIMATIC_FONT")):
        if candidate:
            return candidate
    return None


def _can_render_devanagari(font: "ImageFont.FreeTypeFont") -> bool:
    """Return True if ``font`` actually covers a Devanagari codepoint.

    Uses ``getmask`` for the probe glyph: a covered glyph produces a non-empty
    bitmap, whereas a missing glyph (tofu / .notdef) has an empty or zero-area
    bounding box. Also checks the font exposes the codepoint via ``getbbox``.
    """
    try:
        bbox = font.getbbox(_DEVANAGARI_PROBE)
    except Exception:  # noqa: BLE001 - treat any failure as "cannot render"
        return False
    if bbox is None:
        return False
    # A tofu/.notdef box is typically degenerate; require positive area.
    width = bbox[2] - bbox[0]
    height = bbox[3] - bbox[1]
    if width <= 0 or height <= 0:
        return False
    try:
        mask = font.getmask(_DEVANAGARI_PROBE)
    except Exception:  # noqa: BLE001
        return False
    # An empty mask (no inked pixels) means the glyph is absent.
    return bool(mask.getbbox())


def resolve_devanagari_font(
    cli_font: str | None,
    config_font: str | None,
    size: int,
    *,
    env: dict | None = None,
) -> "ImageFont.FreeTypeFont":
    """Resolve and load a Devanagari-capable TrueType font.

    Raises :class:`FontError` with an actionable message when no font path is
    supplied, when the path does not exist, when it cannot be loaded as a
    TrueType font, or when the font cannot render Devanagari. Never falls back
    to the PIL default bitmap font.
    """
    path = resolve_font_path(cli_font, config_font, env=env)
    if not path:
        raise FontError(_FONT_HELP)
    if not os.path.isfile(path):
        raise FontError(f"Font file not found: {path}\n\n{_FONT_HELP}")
    try:
        font = ImageFont.truetype(path, size)
    except Exception as exc:  # noqa: BLE001 - surface a clear error
        raise FontError(
            f"Could not load {path!r} as a TrueType font: {exc}\n\n{_FONT_HELP}"
        ) from exc
    if not _can_render_devanagari(font):
        raise FontError(
            f"The font {path!r} does not cover Devanagari (U+0915 KA has no "
            f"glyph), so burned-in subtitles would be tofu boxes.\n\n{_FONT_HELP}"
        )
    return font


# ---------------------------------------------------------------------------
# Caption drawing
# ---------------------------------------------------------------------------

def _text_size(draw: "ImageDraw.ImageDraw", text: str, font) -> tuple[int, int]:
    bbox = draw.textbbox((0, 0), text, font=font)
    return bbox[2] - bbox[0], bbox[3] - bbox[1]


def wrap_text(draw, text: str, font, max_width: int) -> list[str]:
    """Greedy word-wrap ``text`` to lines no wider than ``max_width`` pixels."""
    words = text.split()
    if not words:
        return [text]
    lines: list[str] = []
    current = words[0]
    for word in words[1:]:
        trial = f"{current} {word}"
        if _text_size(draw, trial, font)[0] <= max_width:
            current = trial
        else:
            lines.append(current)
            current = word
    lines.append(current)
    return lines


def draw_caption(frame: "Image.Image", text: str, font, *, margin_frac: float = 0.06):
    """Burn a wrapped, centred lower-third caption onto ``frame`` in place.

    Draws a semi-transparent background band behind the text and an outline
    stroke around each line for legibility over any background.
    """
    if not text:
        return
    W, H = frame.size
    draw = ImageDraw.Draw(frame)
    max_width = int(W * (1 - 2 * margin_frac))
    lines = wrap_text(draw, text, font, max_width)

    line_heights = [_text_size(draw, ln, font)[1] for ln in lines]
    line_gap = max(2, int(font.size * 0.25))
    text_block_h = sum(line_heights) + line_gap * (len(lines) - 1)

    pad = max(6, int(font.size * 0.4))
    band_top = H - int(H * margin_frac) - text_block_h - 2 * pad
    band_top = max(0, band_top)
    band_bottom = H - int(H * margin_frac) + pad
    band_bottom = min(H, band_bottom)

    # Semi-transparent band for contrast.
    overlay = Image.new("RGBA", frame.size, (0, 0, 0, 0))
    od = ImageDraw.Draw(overlay)
    od.rectangle((0, band_top, W, band_bottom), fill=(0, 0, 0, 140))
    frame.paste(
        Image.alpha_composite(frame.convert("RGBA"), overlay).convert("RGB"), (0, 0)
    )

    draw = ImageDraw.Draw(frame)
    y = band_top + pad
    stroke = max(1, int(font.size * 0.08))
    for ln, lh in zip(lines, line_heights):
        lw = _text_size(draw, ln, font)[0]
        x = (W - lw) // 2
        draw.text(
            (x, y), ln, font=font, fill=(255, 255, 255),
            stroke_width=stroke, stroke_fill=(0, 0, 0),
        )
        y += lh + line_gap
