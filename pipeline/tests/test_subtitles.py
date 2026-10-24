"""SRT timestamp formatting + sidecar writer tests."""
from pipeline.subtitles import srt_time, write_srt


def test_srt_time_zero():
    assert srt_time(0) == "00:00:00,000"


def test_srt_time_milliseconds():
    assert srt_time(1.234) == "00:00:01,234"


def test_srt_time_minute_second_boundary():
    assert srt_time(61.5) == "00:01:01,500"


def test_srt_time_hours():
    assert srt_time(3661.007) == "01:01:01,007"


def test_srt_time_rounds_ms():
    # 0.0016s rounds to 2ms
    assert srt_time(0.0016) == "00:00:00,002"


def test_write_srt_structure_and_unicode(tmp_path):
    path = tmp_path / "out.srt"
    events = [
        (0.0, 1.0, "वो क्या है?"),
        (2.0, 3.5, "ये नॉर्मल है।"),
    ]
    write_srt(str(path), events)
    text = path.read_text(encoding="utf-8")

    assert "1\n00:00:00,000 --> 00:00:01,000\nवो क्या है?\n\n" in text
    assert "2\n00:00:02,000 --> 00:00:03,500\nये नॉर्मल है।\n\n" in text
    # Ordering: index 1 appears before index 2.
    assert text.index("वो क्या है?") < text.index("ये नॉर्मल है।")
