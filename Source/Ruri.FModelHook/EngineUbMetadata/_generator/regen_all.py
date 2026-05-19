"""Regenerate all 8 UE 5.0-5.7 seed packs in one shot.

Reads source trees from `D:\\GameStudy\\UnrealEngine-<X.Y.Z>-release\\` and
writes per-version seeds under the sibling `EngineUbMetadata\\GAME_UE5_<X>\\`
folders. Calls `gen_ub_metadata.py` per version.

Why a separate helper instead of just invoking the generator 8 times:

  * Each call loads ~100k constants and walks ~3-4k source files — running
    them serially in one Python process amortises nothing but at least
    keeps the log linear and easy to grep.
  * Forgetting `--emit-shader-type-seeds` on any one of them produces a
    stealth-incomplete pack (the layout JSONs land but the hash-to-name
    indexes don't), so the flag lives here in one place.

Adjust `VERSIONS` if you've dumped UE source under different paths.
"""
import subprocess
import sys
from pathlib import Path

GEN = Path(__file__).resolve().parent / "gen_ub_metadata.py"
OUT_ROOT = Path(__file__).resolve().parent.parent  # parent of GAME_UE5_X dirs

VERSIONS = [
    ("GAME_UE5_0", r"D:\GameStudy\UnrealEngine-5.0.3-release", "5.0.3"),
    ("GAME_UE5_1", r"D:\GameStudy\UnrealEngine-5.1.1-release", "5.1.1"),
    ("GAME_UE5_2", r"D:\GameStudy\UnrealEngine-5.2.1-release", "5.2.1"),
    ("GAME_UE5_3", r"D:\GameStudy\UnrealEngine-5.3.2-release", "5.3.2"),
    ("GAME_UE5_4", r"D:\GameStudy\UnrealEngine-5.4.4-release", "5.4.4"),
    ("GAME_UE5_5", r"D:\GameStudy\UnrealEngine-5.5.4-release", "5.5.4"),
    ("GAME_UE5_6", r"D:\GameStudy\UnrealEngine-5.6.1-release", "5.6.1"),
    ("GAME_UE5_7", r"D:\GameStudy\UnrealEngine-5.7.4-release", "5.7.4"),
]

for folder, src, ver in VERSIONS:
    print(f"=== {folder} (UE {ver}) ===", flush=True)
    if not Path(src).exists():
        print(f"!! source tree missing for {folder}: {src} — skipping", flush=True)
        continue
    try:
        subprocess.run(
            [sys.executable, "-u", str(GEN),
             "--engine-src", src,
             "--engine-version", ver,
             "--out-dir", str(OUT_ROOT),
             "--target-folder", folder,
             "--emit-shader-type-seeds"],
            check=True,
            stdout=sys.stdout,
            stderr=sys.stderr,
        )
    except subprocess.CalledProcessError as e:
        print(f"!! generator failed for {folder}: exit {e.returncode}", flush=True)
        sys.exit(e.returncode)

print("=== ALL DONE ===", flush=True)
