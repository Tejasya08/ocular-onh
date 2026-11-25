#!/usr/bin/env python3
"""
Ocular -- the drop folder. Put an image in, get it in the simulator.

    python Tools/ocular_watch.py

Leave it running. Every JPEG or PNG dropped into Inbox/ is sent to Tripo,
segmented, fitted, and loaded into a freshly built simulator. The processed
image moves to Inbox/done/ so it is not picked up twice.

Options worth knowing:

    --inbox DIR      watch somewhere else (default: <project>/Inbox)
    --eye OD|OS      which eye the drops are. Or encode it in the filename --
                     "jones_OS.jpg" is detected and wins over this flag.
    --no-tripo       fit the photograph only; keep the globe already installed
    --no-launch      process and build nothing; useful with the Editor open
    --once           process whatever is already in the folder, then exit

FILENAME IS THE PATIENT NAME. "A_Jones_OD.png" becomes a study called
"A Jones OD" for the eye OS/OD detected in it. Rename before dropping rather
than renaming after.

WHY EACH DROP TAKES A FEW MINUTES. Tripo generation is around a minute, the
Unity import and fit another thirty seconds, and the player rebuild about a
minute after that. The rebuild is unavoidable for a standalone player: studies
and models load from Resources, and Resources is baked into the build at build
time. If you want the fast loop, keep the Unity Editor open and run with
--no-launch -- the Editor picks up new assets on its own and you just press
play.
"""

import argparse
import os
import re
import subprocess
import sys
import time

PROJECT_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PIPELINE = os.path.join(PROJECT_ROOT, "Tools", "ocular_pipeline.py")

IMAGE_TYPES = (".png", ".jpg", ".jpeg")

POLL_SECONDS = 2

# A file is processed only once its size has stopped changing. Dropping a
# 15 MB photograph over a network share, or having a camera write it directly,
# means the file EXISTS long before it is complete -- and a half-written JPEG
# segments into nonsense without ever looking like an error.
STABLE_CHECKS = 2


def detect_eye(filename, default):
    """OD/OS from the filename if it is in there, otherwise the flag."""
    match = re.search(r"(?:^|[_\-\s])(OD|OS)(?:$|[_\-\s.])", filename, re.IGNORECASE)
    return match.group(1).upper() if match else default


def patient_name(filename):
    stem = os.path.splitext(os.path.basename(filename))[0]
    return re.sub(r"[_\-]+", " ", stem).strip() or "Imported eye"


def is_stable(path, seen_sizes):
    """True once the file's size has repeated STABLE_CHECKS times."""
    try:
        size = os.path.getsize(path)
    except OSError:
        return False

    history = seen_sizes.setdefault(path, [])
    history.append(size)

    if len(history) > STABLE_CHECKS + 1:
        history.pop(0)

    return len(history) > STABLE_CHECKS and len(set(history[-(STABLE_CHECKS + 1):])) == 1


def process(path, args):
    name = patient_name(path)
    eye = detect_eye(os.path.basename(path), args.eye)

    print("\n" + "=" * 68)
    print("Processing: {}   ({})".format(name, eye))
    print("=" * 68)

    command = [
        sys.executable, PIPELINE,
        "--image", path,
        "--name", name,
        "--eye", eye,
        "--axial-length", str(args.axial_length),
        "--field-deg", str(args.field_deg),
    ]

    if args.no_tripo:
        command.append("--no-tripo")
    if args.no_launch:
        command.append("--no-launch")

    result = subprocess.run(command)
    return result.returncode == 0


def retire(path, done_dir, ok):
    """
    Moves a handled image out of the watch folder.
    Failures go to done/failed/ rather than staying put -- an image that keeps
    failing would otherwise be retried forever on every poll.
    """
    target_dir = done_dir if ok else os.path.join(done_dir, "failed")
    os.makedirs(target_dir, exist_ok=True)

    target = os.path.join(target_dir, os.path.basename(path))

    # Never clobber a previous drop of the same name.
    stem, extension = os.path.splitext(target)
    counter = 2
    while os.path.exists(target):
        target = "{}_{}{}".format(stem, counter, extension)
        counter += 1

    try:
        os.replace(path, target)
    except OSError as exc:
        print("  Could not move {} out of the inbox: {}".format(path, exc))


def main():
    parser = argparse.ArgumentParser(description="Watch a folder and run the Ocular pipeline on drops.")
    parser.add_argument("--inbox", default=os.path.join(PROJECT_ROOT, "Inbox"))
    parser.add_argument("--eye", choices=["OD", "OS"], default="OD",
                        help="Default eye. A filename containing OD or OS overrides it.")
    parser.add_argument("--field-deg", type=float, default=30.0)
    parser.add_argument("--axial-length", type=float, default=24.0)
    parser.add_argument("--no-tripo", action="store_true")
    parser.add_argument("--no-launch", action="store_true")
    parser.add_argument("--once", action="store_true",
                        help="Process what is already there, then exit.")

    args = parser.parse_args()

    inbox = os.path.abspath(args.inbox)
    done_dir = os.path.join(inbox, "done")

    os.makedirs(inbox, exist_ok=True)

    # Checked once, up front. Discovering a missing key after a clinician has
    # already dropped an image means the drop is consumed and the run wasted.
    if not args.no_tripo and not os.environ.get("TRIPO_API_KEY"):
        print("TRIPO_API_KEY is not set.\n"
              "  A Tripo subscription is not the same thing as an API key -- get one from\n"
              "  https://platform.tripo3d.ai under API Keys, then:\n"
              '      setx TRIPO_API_KEY "tsk_your_key_here"\n'
              "  and open a NEW terminal (setx does not affect the current one).\n"
              "  Or run with --no-tripo to fit photographs without generating models.")
        return 3

    print("Ocular watch folder")
    print("  Drop JPEG or PNG files into: " + inbox)
    print("  Filename becomes the patient name; OD/OS in it picks the eye.")
    print("  Handled images move to: " + done_dir)
    if args.no_tripo:
        print("  Tripo generation DISABLED (--no-tripo).")
    if args.no_launch:
        print("  Build and launch DISABLED (--no-launch).")
    print("  Ctrl-C to stop.\n")

    seen_sizes = {}

    try:
        while True:
            try:
                entries = sorted(os.listdir(inbox))
            except OSError as exc:
                print("Cannot read the inbox: {}".format(exc))
                return 4

            pending = [os.path.join(inbox, e) for e in entries
                       if e.lower().endswith(IMAGE_TYPES)
                       and os.path.isfile(os.path.join(inbox, e))]

            for path in pending:
                if not is_stable(path, seen_sizes):
                    continue

                seen_sizes.pop(path, None)
                ok = process(path, args)
                retire(path, done_dir, ok)

                print("  {} -> {}".format(os.path.basename(path),
                                           "done" if ok else "FAILED (moved to done/failed)"))

            if args.once:
                # One settling pass so a file mid-copy still gets a chance.
                if not pending:
                    break
                time.sleep(POLL_SECONDS)
                if not [p for p in pending if os.path.exists(p)]:
                    break

            time.sleep(POLL_SECONDS)

    except KeyboardInterrupt:
        print("\nStopped.")

    return 0


if __name__ == "__main__":
    sys.exit(main())
