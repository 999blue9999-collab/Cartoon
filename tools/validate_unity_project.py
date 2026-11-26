#!/usr/bin/env python3
"""Check the Unity project is buildable by Unity Cloud / Build Automation.

Run: python3 tools/validate_unity_project.py
No Unity install required — this verifies the committed YAML/meta/GUID chain.
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SCENE = ROOT / "Assets" / "Scenes" / "NoreDemo.unity"
SCENE_META = ROOT / "Assets" / "Scenes" / "NoreDemo.unity.meta"
BUILD_SETTINGS = ROOT / "ProjectSettings" / "EditorBuildSettings.asset"
PROJECT_VERSION = ROOT / "ProjectSettings" / "ProjectVersion.txt"
CLOUD_SETUP = ROOT / "Assets" / "Editor" / "CloudBuildSetup.cs"
DATA_DIR = ROOT / "Assets" / "Resources" / "Data"
DATA_FILES = ["config", "buildings", "races", "units", "heroes", "skills", "gear", "dragons", "world"]

errors: list[str] = []
checks = 0


def check(condition: bool, message: str) -> None:
    global checks
    checks += 1
    if not condition:
        errors.append(message)


def main() -> int:
    check(SCENE.exists(), "missing Assets/Scenes/NoreDemo.unity")
    check(SCENE_META.exists(), "missing Assets/Scenes/NoreDemo.unity.meta")
    check(BUILD_SETTINGS.exists(), "missing ProjectSettings/EditorBuildSettings.asset")
    check(PROJECT_VERSION.exists(), "missing ProjectSettings/ProjectVersion.txt")
    check(CLOUD_SETUP.exists(), "missing Assets/Editor/CloudBuildSetup.cs")

    if SCENE.exists():
        scene = SCENE.read_text()
        check(scene.startswith("%YAML 1.1"), "scene must start with %YAML 1.1")
        check("!u!1660057539" in scene and "SceneRoots:" in scene, "scene has no SceneRoots document")
        check("m_Roots: []" in scene, "scene is not empty (bootstrap needs no GameObjects)")

    guid = ""
    if SCENE_META.exists():
        meta = SCENE_META.read_text()
        m = re.search(r"^guid:\s*([0-9a-fA-F]{32})\s*$", meta, re.M)
        check(m is not None, "scene .meta has no 32-hex guid")
        if m:
            guid = m.group(1)
        check("DefaultImporter:" in meta, "scene .meta should use DefaultImporter")

    if BUILD_SETTINGS.exists():
        build = BUILD_SETTINGS.read_text()
        check("!u!1045" in build, "EditorBuildSettings has wrong class id")
        check("path: Assets/Scenes/NoreDemo.unity" in build, "build settings do not list NoreDemo.unity")
        check("enabled: 1" in build, "NoreDemo.unity is not enabled in build settings")
        if guid:
            check(f"guid: {guid}" in build, f"build settings guid does not match scene meta guid {guid}")

    if PROJECT_VERSION.exists():
        version = PROJECT_VERSION.read_text()
        check("m_EditorVersion: 6000.0." in version, "ProjectVersion.txt is not Unity 6 (6000.0.x)")

    if CLOUD_SETUP.exists():
        cs = CLOUD_SETUP.read_text()
        check("[InitializeOnLoad]" in cs, "CloudBuildSetup has no [InitializeOnLoad] safety net")
        check("EditorApplication.delayCall" in cs, "CloudBuildSetup should defer AssetDatabase work")
        check("public static void Build()" in cs, "CloudBuildSetup has no -executeMethod Build entry point")
        check("public static void Verify()" in cs, "CloudBuildSetup has no Verify pre-build gate")
        check("EditorBuildSettings.scenes = scenes.ToArray()" in cs, "CloudBuildSetup does not repair build settings")

    for name in DATA_FILES:
        p = DATA_DIR / f"{name}.json"
        check(p.exists(), f"missing data file {name}.json")
        if p.exists():
            try:
                json.loads(p.read_text())
            except json.JSONDecodeError as e:
                check(False, f"{name}.json is invalid JSON: {e}")

    gitignore = (ROOT / ".gitignore").read_text()
    check("[Bb]uilds/" in gitignore, ".gitignore does not ignore Builds/")

    print(f"Unity project: {checks} checks, {len(errors)} error(s)")
    if guid:
        print(f"Build scene GUID: {guid}")
    for e in errors:
        print("FAIL  " + e)
    if errors:
        return 1
    print("OK — scene, meta, EditorBuildSettings, Unity version, cloud build hooks and data are consistent.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
