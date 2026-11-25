#!/usr/bin/env python3
"""
Ocular -- one image in, a running simulation out.

    python Tools/ocular_pipeline.py --image C:\\path\\to\\eye.jpg --name "Patient A" --eye OD

What it does, in order:

    1. Copies the fundus photograph into the project.
    2. Sends it to Tripo AI, waits for the 3D model, downloads it as FBX.
    3. Runs Unity in batch mode to segment the disc, fit the parametric nerve
       head and write the study asset.
    4. Rebuilds the player and launches it.

TWO MODELS COME OUT OF ONE IMAGE, AND THEY ARE NOT THE SAME KIND OF THING.
This is the single most important thing to understand about this pipeline.

    The TRIPO mesh is GENERATIVE. A neural network invented its geometry to be
    plausible given the picture. Its depths are not this patient's depths; they
    are not anyone's depths. It is scenery, and the simulator treats it as
    scenery -- it loads through EyeGlobeView, which exists for exactly this.

    The MEASURED nerve head is FITTED. The disc and cup margins are traced off
    the real photograph, fitted to an OpticNerveHeadSpec, and regenerated as a
    height field that the measurement engine then samples. Every number on
    screen comes from that surface.

Putting a generated mesh anywhere near the measurements would hand a clinician
invented numbers wearing the costume of measured ones. The whole project is
built to prevent that, and so is this script.

A fundus photograph is also, frankly, a poor input for Tripo: it is a flat
circular view of the inside of the back of an eye, with no subject to separate
from its background, and Tripo is trained on objects. Expect a disc-shaped
lump. That is fine for context and useless as anatomy -- which is why
--no-tripo exists, and why reusing one good globe across patients is usually
the better call than generating a fresh blob per patient at credit cost.

API: Tripo v3 (https://openapi.tripo3d.ai/v3). v2 is retired as of
2026-11-01, so nothing here speaks v2.

Requires the TRIPO_API_KEY environment variable. Standard library only.
"""

import argparse
import json
import mimetypes
import os
import re
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid

BASE_URL = "https://openapi.tripo3d.ai/v3"

PROJECT_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

UNITY_EXE = os.environ.get(
    "OCULAR_UNITY_EXE",
    r"C:\Program Files\Unity\Hub\Editor\6000.0.25f1\Editor\Unity.exe")

PHOTO_DIR = os.path.join(PROJECT_ROOT, "Assets", "Resources", "FundusPhotos")
GLOBE_DIR = os.path.join(PROJECT_ROOT, "Assets", "Resources", "EyeGlobe")
# Project root, not under Assets: anywhere inside Resources this would be
# imported as a TextAsset and shipped inside the player, carrying the patient's
# name and clinical context into a distributable build.
JOB_PATH = os.path.join(PROJECT_ROOT, "pipeline_job.json")

# Tripo polls: generation is usually under a minute, but queue time is not
# bounded by anything we control.
POLL_SECONDS = 5
POLL_TIMEOUT_SECONDS = 900


# ---------------------------------------------------------------------------
# Tripo client
# ---------------------------------------------------------------------------

class TripoError(RuntimeError):
    pass


def _auth_headers(api_key):
    return {"Authorization": "Bearer " + api_key}


def _request(url, api_key, data=None, headers=None, method=None):
    """One JSON call. Raises TripoError on a non-zero `code` or an HTTP error."""
    hdrs = _auth_headers(api_key)
    if headers:
        hdrs.update(headers)

    req = urllib.request.Request(url, data=data, headers=hdrs, method=method)

    try:
        with urllib.request.urlopen(req, timeout=120) as response:
            payload = json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        body = exc.read().decode("utf-8", "replace")[:600]
        raise TripoError("HTTP {} from {}\n{}".format(exc.code, url, body))
    except urllib.error.URLError as exc:
        raise TripoError("Could not reach {}: {}".format(url, exc.reason))

    # Tripo signals application errors in the body with a non-zero `code`, not
    # with an HTTP status. A 200 here is not success on its own.
    if payload.get("code") != 0:
        raise TripoError("Tripo returned code {}: {}".format(
            payload.get("code"), payload.get("message", payload)))

    return payload.get("data", {})


# What one eye costs, in credits, for the exact request this script makes:
# image-to-3D with standard texture (30) plus the quad add-on (5).
#
# The quad add-on is cheaper than the alternative, not an extravagance. Without
# it the output is GLB at 30 credits and then needs a Format Convert to FBX,
# which bills at the Advanced rate (10) because this script passes non-default
# texture_size and texture_format -- 40 credits and a second round trip, versus
# 35 and one.
ESTIMATED_CREDITS_PER_EYE = 35


def get_balance(api_key):
    """Available credits. Returns None if the balance cannot be read."""
    try:
        data = _request(BASE_URL + "/account/balance", api_key)
        return float(data.get("balance", 0.0))
    except (TripoError, TypeError, ValueError):
        return None


def check_credit(api_key):
    """
    Preflight. Raises TripoError when the balance clearly cannot cover one eye.

    <para>Checked before the upload rather than after, because the API accepts
    the file and only rejects at task creation -- so without this the clinician
    waits through an upload to be told the account was empty before they
    started. A Tripo Studio subscription does not carry API credit; the two are
    bought separately, which is the single most surprising thing about getting
    this running.</para>
    """
    balance = get_balance(api_key)

    if balance is None:
        return  # Unreadable balance is not a reason to refuse to try.

    if balance < ESTIMATED_CREDITS_PER_EYE:
        raise TripoError(
            "Tripo balance is {:.0f} credits; one eye costs about {} "
            "(${:.2f}).\n     Top up at https://developers.tripo3d.ai/en/pricing "
            "-- from $1, and a Tripo Studio\n     subscription does NOT include API "
            "credit, it is purchased separately.".format(
                balance, ESTIMATED_CREDITS_PER_EYE,
                ESTIMATED_CREDITS_PER_EYE * 0.01))

    print("  Tripo balance: {:.0f} credits (~{:.0f} eyes)".format(
        balance, balance / ESTIMATED_CREDITS_PER_EYE))


def upload_image(api_key, image_path):
    """POST /files -> file_token. multipart/form-data, field name `file`."""
    with open(image_path, "rb") as handle:
        content = handle.read()

    if len(content) > 20 * 1024 * 1024:
        raise TripoError("Tripo caps uploads at 20 MB; this image is {:.1f} MB."
                         .format(len(content) / 1048576.0))

    mime = mimetypes.guess_type(image_path)[0] or "application/octet-stream"
    boundary = "----OcularBoundary" + uuid.uuid4().hex
    name = os.path.basename(image_path)

    body = b"".join([
        ("--%s\r\n" % boundary).encode(),
        ('Content-Disposition: form-data; name="file"; filename="%s"\r\n' % name).encode(),
        ("Content-Type: %s\r\n\r\n" % mime).encode(),
        content,
        ("\r\n--%s--\r\n" % boundary).encode(),
    ])

    data = _request(
        BASE_URL + "/files", api_key, data=body,
        headers={"Content-Type": "multipart/form-data; boundary=" + boundary})

    token = data.get("file_token")
    if not token:
        raise TripoError("Upload succeeded but returned no file_token: %r" % data)

    return token


def create_image_to_model(api_key, file_token, face_limit):
    """
    POST /generation/image-to-model -> task_id.

    quad=true is not about topology here -- it is the documented way to make
    the OUTPUT FORMAT FBX. Unity imports FBX natively and cannot import GLB
    without glTFast, so this one flag removes both a conversion task (more
    credits, more waiting) and a package dependency. Unity triangulates the
    quads on import.
    """
    payload = {
        "input": file_token,
        "model": "v3.1-20260211",
        "texture": True,
        "pbr": True,
        "quad": True,
        "face_limit": face_limit,
    }

    data = _request(
        BASE_URL + "/generation/image-to-model", api_key,
        data=json.dumps(payload).encode("utf-8"),
        headers={"Content-Type": "application/json"})

    return data["task_id"]


def create_convert_to_fbx(api_key, task_id):
    """POST /models/convert -> task_id. Fallback when generation returned GLB."""
    payload = {
        "input": task_id,
        "format": "FBX",
        "texture_size": 2048,
        "texture_format": "PNG",
        "fbx_preset": "blender",
    }

    data = _request(
        BASE_URL + "/models/convert", api_key,
        data=json.dumps(payload).encode("utf-8"),
        headers={"Content-Type": "application/json"})

    return data["task_id"]


def wait_for_task(api_key, task_id, label):
    """GET /tasks/{id} until it leaves queued/running. Returns the output dict."""
    deadline = time.time() + POLL_TIMEOUT_SECONDS
    last_progress = -1

    while time.time() < deadline:
        data = _request(BASE_URL + "/tasks/" + task_id, api_key)

        status = data.get("status")
        progress = data.get("progress", 0)

        if progress != last_progress:
            print("    {}: {} {}%".format(label, status, progress))
            last_progress = progress

        if status == "success":
            credits = data.get("credits_consumed")
            if credits is not None:
                print("    {}: done, {} credits".format(label, credits))
            return data.get("output", {})

        if status in ("failed", "cancelled"):
            raise TripoError("{} {}: {} ({})".format(
                label, status,
                data.get("error_message", "no message"),
                data.get("error_code", "no code")))

        time.sleep(POLL_SECONDS)

    raise TripoError("{} did not finish within {} s (task {})".format(
        label, POLL_TIMEOUT_SECONDS, task_id))


def download(url, destination):
    """Streams a result file to disk. Tripo's CDN links are time-limited."""
    os.makedirs(os.path.dirname(destination), exist_ok=True)

    try:
        with urllib.request.urlopen(url, timeout=300) as response, \
                open(destination, "wb") as out:
            shutil.copyfileobj(response, out)
    except urllib.error.URLError as exc:
        raise TripoError("Could not download {}: {}".format(url, exc))

    return os.path.getsize(destination)


def generate_globe(api_key, image_path, slug, face_limit):
    """The whole Tripo leg. Returns the path of the downloaded FBX."""
    check_credit(api_key)

    print("  Uploading photograph...")
    token = upload_image(api_key, image_path)

    print("  Generating 3D model...")
    task_id = create_image_to_model(api_key, token, face_limit)
    output = wait_for_task(api_key, task_id, "generate")

    model_url = output.get("model_url")
    if not model_url:
        raise TripoError("Task finished with no model_url: %r" % output)

    # quad=true should already have produced FBX. Verified rather than assumed:
    # a silently-GLB file lands in the project as an unimportable asset and the
    # globe simply never appears, with nothing anywhere saying why.
    if ".fbx" not in model_url.lower().split("?")[0]:
        print("  Output was not FBX; converting...")
        convert_id = create_convert_to_fbx(api_key, task_id)
        output = wait_for_task(api_key, convert_id, "convert")
        model_url = output.get("model_url", model_url)

    destination = os.path.join(GLOBE_DIR, slug + ".fbx")
    size = download(model_url, destination)

    print("  Saved {} ({:.1f} MB)".format(destination, size / 1048576.0))
    return destination


# ---------------------------------------------------------------------------
# Unity
# ---------------------------------------------------------------------------

def run_unity(method, log_name):
    """
    One batch-mode invocation.

    Unity's exit code is not trustworthy on its own -- it can report 0 while
    having done nothing at all -- so every caller checks for the artefact it
    expected rather than believing this return value.
    """
    log_path = os.path.join(PROJECT_ROOT, log_name)

    command = [
        UNITY_EXE, "-batchmode", "-quit",
        "-projectPath", PROJECT_ROOT,
        "-executeMethod", method,
        "-logFile", log_path,
    ]

    print("  Unity: {}".format(method))
    result = subprocess.run(command)

    errors = []
    if os.path.exists(log_path):
        with open(log_path, "r", encoding="utf-8", errors="replace") as handle:
            for line in handle:
                if "error CS" in line or "Exception:" in line:
                    errors.append(line.rstrip())

    if errors:
        print("  Unity reported problems:")
        for line in errors[:12]:
            print("    " + line)

    return result.returncode, log_path


def slugify(text):
    slug = re.sub(r"[^A-Za-z0-9]+", "_", text).strip("_")
    return slug or "study"


# ---------------------------------------------------------------------------

def main():
    parser = argparse.ArgumentParser(
        description="Fundus photograph -> fitted nerve head + Tripo globe -> running sim.")

    parser.add_argument("--image", required=True, help="Fundus photograph (JPEG or PNG).")
    parser.add_argument("--name", default=None, help="Display name. Defaults to the filename.")
    parser.add_argument("--eye", choices=["OD", "OS"], default="OD", help="Which eye.")
    parser.add_argument("--age", type=int, default=60)
    parser.add_argument("--iop", type=float, default=15.0, help="Goldmann IOP, mmHg.")
    parser.add_argument("--cct", type=float, default=545.0, help="Central corneal thickness, um.")

    parser.add_argument("--field-deg", type=float, default=30.0,
                        help="Fundus camera field angle. 30 and 45 are the usual settings.")
    parser.add_argument("--axial-length", type=float, default=24.0,
                        help="Axial length in mm. Matters: it scales the whole image.")
    parser.add_argument("--cup-depth", type=float, default=None,
                        help="OCT cup depth in mm. Without it, depth is a population "
                             "estimate and is labelled ASSUMED everywhere.")

    parser.add_argument("--face-limit", type=int, default=10000,
                        help="Tripo polycount. Keep low: integrated GPU and a WebGL target.")

    parser.add_argument("--no-tripo", action="store_true",
                        help="Skip generation and keep whatever globe is already installed.")
    parser.add_argument("--no-launch", action="store_true",
                        help="Do everything except build and run the player.")

    args = parser.parse_args()

    image_path = os.path.abspath(args.image)
    if not os.path.isfile(image_path):
        print("No such image: " + image_path)
        return 2

    name = args.name or os.path.splitext(os.path.basename(image_path))[0]
    slug = slugify(name)

    print("Ocular pipeline: {} ({})".format(name, args.eye))

    # Checked before anything is copied. Failing here after having already
    # written a patient's photograph into the project leaves the repository
    # dirty with clinical data as the price of a missing environment variable.
    api_key = None
    if not args.no_tripo:
        api_key = os.environ.get("TRIPO_API_KEY")
        if not api_key:
            print("\nTRIPO_API_KEY is not set. Get a key from "
                  "https://platform.tripo3d.ai (API Keys), then:\n"
                  '    setx TRIPO_API_KEY "tsk_your_key_here"\n'
                  "and open a new terminal. Or re-run with --no-tripo to skip "
                  "generation and fit the photograph only.")
            return 3

    # --- 1. the photograph -------------------------------------------------
    os.makedirs(PHOTO_DIR, exist_ok=True)
    extension = os.path.splitext(image_path)[1].lower()
    if extension not in (".png", ".jpg", ".jpeg"):
        print("Tripo and Unity both want PNG or JPEG; got " + extension)
        return 2

    photo_destination = os.path.join(PHOTO_DIR, slug + extension)
    shutil.copyfile(image_path, photo_destination)
    print("  Photograph -> " + photo_destination)

    # --- 2. Tripo ----------------------------------------------------------
    if args.no_tripo:
        print("  Skipping Tripo (--no-tripo).")
    else:
        os.makedirs(GLOBE_DIR, exist_ok=True)

        # One globe at a time. EyeGlobeView takes the first model alphabetically,
        # so leaving the previous patient's eye in place would quietly win.
        for stale in os.listdir(GLOBE_DIR):
            if stale.lower().endswith((".fbx", ".glb", ".obj")):
                os.remove(os.path.join(GLOBE_DIR, stale))

        try:
            generate_globe(api_key, photo_destination, slug, args.face_limit)
        except TripoError as exc:
            print("\n  Tripo failed: {}\n  Continuing without a globe -- the fitted "
                  "nerve head and its measurements do not depend on it.".format(exc))

    # --- 3. one patient at a time -----------------------------------------
    # StudyAutoLoader opens the alphabetically-last study, which is a fine
    # tiebreak and a terrible way to mean "the one I just imported": a leftover
    # Smoke_Test_OD outranks Patient_A forever, so the simulator would keep
    # opening someone else's eye while the pipeline reported success every time.
    # Clearing first makes "newest" unambiguous. The _fit.txt reports are the
    # audit trail; copy them out if you want a history.
    study_dir = os.path.join(PROJECT_ROOT, "Assets", "Resources", "FundusStudies")
    if os.path.isdir(study_dir):
        for stale in os.listdir(study_dir):
            if stale.startswith(slug + "."):
                continue
            if stale.endswith((".asset", ".asset.meta")):
                os.remove(os.path.join(study_dir, stale))

    # The photographs too. Everything under Resources is baked into the player
    # whether or not anything references it, so a previous patient's fundus
    # image left here does not merely waste space -- it ships inside a
    # distributable build as orphaned clinical data.
    for stale in os.listdir(PHOTO_DIR):
        if stale.startswith(slug + "."):
            continue
        if stale.lower().endswith((".png", ".jpg", ".jpeg", ".meta")):
            os.remove(os.path.join(PHOTO_DIR, stale))

    # --- 4. the job file Unity reads --------------------------------------
    job = {
        "displayName": name,
        "slug": slug,
        "laterality": args.eye,
        "patientAge": args.age,
        "iopMmHg": args.iop,
        "centralCornealThicknessUm": args.cct,
        "fieldAngleDeg": args.field_deg,
        "axialLengthMm": args.axial_length,
        "photoResourcePath": "FundusPhotos/" + slug,
        "photoAssetPath": "Assets/Resources/FundusPhotos/" + slug + extension,
        "cupDepthMm": args.cup_depth if args.cup_depth is not None else -1.0,
    }

    os.makedirs(os.path.dirname(JOB_PATH), exist_ok=True)
    with open(JOB_PATH, "w", encoding="utf-8") as handle:
        json.dump(job, handle, indent=2)

    # --- 4. Unity: import, segment, fit -----------------------------------
    print("  Segmenting and fitting...")
    run_unity("Ocular.Editor.AutoPipeline.RunBatch", "pipeline-import.log")

    study_asset = os.path.join(PROJECT_ROOT, "Assets", "Resources",
                               "FundusStudies", slug + ".asset")

    if not os.path.exists(study_asset):
        print("\n  The study asset was not written. See pipeline-import.log.")
        return 4

    report_path = os.path.join(PROJECT_ROOT, "Assets", "Resources",
                               "FundusStudies", slug + "_fit.txt")
    if os.path.exists(report_path):
        with open(report_path, "r", encoding="utf-8", errors="replace") as handle:
            print("\n" + handle.read().strip() + "\n")

    # --- 5. build and run --------------------------------------------------
    if args.no_launch:
        print("Done. Open the project and press play, or re-run without --no-launch.")
        return 0

    print("  Building player...")
    run_unity("Ocular.Editor.OcularBuildPipeline.BuildWindows", "pipeline-build.log")

    exe = os.path.join(PROJECT_ROOT, "Build", "Windows", "Ocular.exe")
    if not os.path.exists(exe):
        print("  Build produced no executable. See pipeline-build.log.")
        return 5

    print("  Launching.")
    subprocess.Popen([exe, "-screen-width", "1280", "-screen-height", "800",
                      "-screen-fullscreen", "0"], cwd=os.path.dirname(exe))

    return 0


if __name__ == "__main__":
    sys.exit(main())
