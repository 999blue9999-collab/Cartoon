"""Font-resolution + Devanagari-capability tests (fast, offline).

These tests never require network and never render a full video. The error
paths (no font, missing file, non-TrueType file, non-Devanagari font) are
deterministic. The success path and the non-Devanagari refusal are exercised
opportunistically when a suitable font is discoverable on the host; otherwise
they are skipped so the suite stays fast and offline.
"""
import glob
import os

import pytest

from pipeline import text as text_mod
from pipeline.text import FontError, resolve_devanagari_font, resolve_font_path


# ---------------------------------------------------------------------------
# resolve_font_path precedence (pure, no I/O)
# ---------------------------------------------------------------------------

def test_resolve_font_path_prefers_cli():
    env = {"ANIMATIC_FONT": "/env/font.ttf"}
    assert resolve_font_path("/cli.ttf", "/cfg.ttf", env=env) == "/cli.ttf"


def test_resolve_font_path_falls_back_to_config():
    env = {"ANIMATIC_FONT": "/env/font.ttf"}
    assert resolve_font_path(None, "/cfg.ttf", env=env) == "/cfg.ttf"


def test_resolve_font_path_falls_back_to_env():
    env = {"ANIMATIC_FONT": "/env/font.ttf"}
    assert resolve_font_path(None, None, env=env) == "/env/font.ttf"


def test_resolve_font_path_none_when_unset():
    assert resolve_font_path(None, None, env={}) is None


# ---------------------------------------------------------------------------
# resolve_devanagari_font error paths
# ---------------------------------------------------------------------------

def test_no_font_raises_actionable_error():
    with pytest.raises(FontError) as exc:
        resolve_devanagari_font(None, None, 32, env={})
    msg = str(exc.value)
    assert "--font" in msg
    assert "NotoSansDevanagari" in msg


def test_missing_font_file_raises():
    with pytest.raises(FontError) as exc:
        resolve_devanagari_font("/does/not/exist.ttf", None, 32, env={})
    msg = str(exc.value)
    assert "not found" in msg.lower()
    assert "--font" in msg


def test_non_truetype_file_raises(tmp_path):
    bogus = tmp_path / "notafont.ttf"
    bogus.write_bytes(b"this is not a font")
    with pytest.raises(FontError) as exc:
        resolve_devanagari_font(str(bogus), None, 32, env={})
    assert "--font" in str(exc.value)


def _find_latin_only_font():
    """Return a path to a Latin font that lacks Devanagari, or None."""
    candidates = sorted(glob.glob("/usr/share/fonts/**/*.ttf", recursive=True))
    for path in candidates:
        try:
            font = __import__("PIL.ImageFont", fromlist=["ImageFont"]).truetype(path, 24)
        except Exception:
            continue
        if not text_mod._can_render_devanagari(font):
            return path
    return None


def test_non_devanagari_font_is_refused():
    latin = _find_latin_only_font()
    if latin is None:
        pytest.skip("no Latin-only TrueType font available to test the refusal")
    with pytest.raises(FontError) as exc:
        resolve_devanagari_font(latin, None, 32, env={})
    msg = str(exc.value)
    assert "Devanagari" in msg
    assert "--font" in msg


# ---------------------------------------------------------------------------
# Success path (only when a Devanagari font is discoverable)
# ---------------------------------------------------------------------------

def _find_devanagari_font():
    candidates = [os.environ.get("ANIMATIC_FONT")]
    candidates += sorted(glob.glob("/projects/sandbox/fonts/*Devanagari*.ttf"))
    candidates += sorted(glob.glob("/usr/share/fonts/**/*Devanagari*.ttf", recursive=True))
    for path in candidates:
        if path and os.path.isfile(path):
            return path
    return None


def test_devanagari_font_loads_and_covers_ka():
    deva = _find_devanagari_font()
    if deva is None:
        pytest.skip("no Devanagari font available to test the success path")
    font = resolve_devanagari_font(deva, None, 32, env={})
    # The font object exists and actually covers U+0915 KA.
    assert font is not None
    assert text_mod._can_render_devanagari(font)
