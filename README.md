# Ocular — glaucoma optic nerve head simulator

A 3D optic nerve head you can inspect the way a clinician would, which **measures**
glaucoma parameters off the geometry rather than reporting the values it was built from.

Unity **6000.0.25f1**, URP 17.0.3. Targets **WebGL**, so one build serves the desktop
mouse view, VR in the Quest browser, and AR on an Android phone through WebXR.

> **Scope.** This is a training and visualisation tool. It is not a diagnostic device,
> it contains no patient data, and its normative bands are illustrative teaching values,
> not a normative database. The cases are fictional.

---

## The central idea

The optic nerve head is **parametric ground truth**: disc diameters, cup-to-disc ratios,
per-sector rim thickness, notch, cup depth and peripapillary atrophy are all authored.
That spec is turned into a continuous height field, tessellated into a mesh, and then the
measurement engine **samples that surface** to recover the numbers — it never reads the
authoring fields.

That round trip is what makes a displayed CDR a measurement rather than an echo of a
slider, and it is asserted directly in `OnhMeasurementTests`.

### Two measurement methods, side by side

| Method | Cup margin defined by | Notes |
|---|---|---|
| `ReferencePlane` | Surface lying below a plane 50 µm under the temporal rim | Heidelberg-style, citable |
| `SlopeContour` | First point where the wall tips past a slope threshold | Approximates the contour judged at the slit lamp |

They **disagree slightly, and that is correct** — the reference plane has to descend
50 µm down the cup wall before it starts counting, so it reads a smaller cup. The
disagreement is a teaching point, and its direction is asserted as a test.

---

## Layout

```
Assets/Scripts/
  Core/          pure millimetre-space science, no Unity units anywhere
    Anatomy/     DiscGeometry, OpticNerveHeadSpec, OnhSurface
    Measurement/ OnhMeasurementEngine, OnhMetrics, DdlsScale,
                 RnflProfile, VisualFieldModel, IopModel
    Imaging/     FundusImage, ImageCalibration, PolarContour,
                 DiscSegmenter, OnhSpecFitter, OnhFitReport
  Data/          GlaucomaCase, CaseLibrary, CasePresets, FundusStudy
  Anatomy/       OnhMeshBuilder, FundusTextureBuilder, FundusPhotoTexture,
                 OnhModelBinding, EyeGlobeView
  Interaction/   OrbitInspector
  Editor/        OcularProjectSettings, OcularSceneGenerator,
                 FundusImportWindow, EyeModelImporter
Assets/Resources/EyeGlobe/   drop an eye model here
Assets/Tests/EditMode/   the correctness gate
```

**Units.** `Ocular.Core` is millimetres end to end and knows nothing about world space.
The conversion happens exactly once, in `OnhMeshBuilder.MmToUnits`. A bug there can make
the eye the wrong size but can never make a reported measurement wrong.

**Angles.** Disc-space, radians: `0 = temporal, π/2 = superior, π = nasal, 3π/2 = inferior`.
Laterality (OD/OS) is presentation only — X is mirrored at render time so that sector
measurements never swap nasal and temporal between eyes.

---

## Running it

Open `D:\Games\Ocular` in Unity 6000.0.25f1 and load `Assets/Scenes/OcularSim.unity`.

If the scene is missing or you changed a case preset:

```bash
"C:\Program Files\Unity\Hub\Editor\6000.0.25f1\Editor\Unity.exe" -batchmode -quit -projectPath "D:\Games\Ocular" -executeMethod Ocular.Editor.OcularSceneGenerator.GenerateBatch -logFile -
```

Or in the editor: **Ocular → Generate Simulation Scene**.

> The scene is generated from code, not hand-authored. **A clean compile does not mean the
> scene changed** — re-run the generator after editing a preset.

### Controls

| Input | Action |
|---|---|
| Left drag | Orbit |
| Middle drag | Pan |
| Wheel | Zoom |
| `1` | En-face view (what a fundus photo shows) |
| `2` | Oblique |
| `3` | Cross-section (shows cup **depth** — the thing a photo cannot) |
| `G` | Eye globe: ghost / sectioned / solid / off |

### Tests

```bash
"C:\Program Files\Unity\Hub\Editor\6000.0.25f1\Editor\Unity.exe" -batchmode -projectPath "D:\Games\Ocular" -runTests -testPlatform EditMode -testResults results.xml -logFile -
```

---

## Importing a real eye

A case can be **fitted from a fundus photograph** instead of authored. **Ocular →
Import Fundus Photograph**, or `FundusStudy` assets directly.

The photograph does not become the model. Traced disc and cup margins are fitted
to an `OpticNerveHeadSpec`, that spec generates a surface exactly as a preset
does, and the measurement engine then reads the surface — so an image-derived CDR
is still a measurement of geometry, and a bad fit shows up as a **residual**
rather than as a confident wrong number. `FundusImportTests` asserts the measured
CDR of a refitted eye against the measured CDR of the eye it was traced from,
never against the fitter's own arithmetic.

```
photograph ─trace─▶ contours ─fit─▶ spec ─▶ OnhSurface ─measure─▶ metrics
                        └──────── residual ◀────────┘
```

The fit is **cup-exact**: the model's disc margin is an ellipse and cannot be
anything else, so a traced disc's departure from an ellipse lands in the disc
residual and the cup margin — which CDR, ISNT and DDLS are all computed from —
is reproduced exactly.

### Three things a photograph cannot tell you

| | Why | What happens |
|---|---|---|
| **Cup depth** | A photograph is a projection; depth is the dimension it discarded | Population estimate from CDR unless you supply an OCT depth, and labelled `ASSUMED` everywhere it appears |
| **Absolute size**, if you calibrated from an assumed disc diameter | You assumed the answer, so it reports back what you assumed | `IsCircular` propagates to `DiscDiameterIsMeasured`; ratios stay valid, disc-size classification is suppressed |
| **The cup margin, from colour** | The cup margin is where the surface bends — read off vessel bends, not pallor | Auto-trace reports its own contrast and flags itself unreliable below it; the window tells you to correct it by hand |

Calibrate from the **camera field angle and axial length**, not from an assumed
disc. The axial-length term is not a refinement: a myopic eye is longer, so its
retinal image is larger and its disc covers more pixels, and skipping the
correction reports every myopic disc as smaller than it is. Myopic discs — large,
tilted — are already the ones that get over-called, so the error runs in the
direction that reinforces the mistake.

### The two orientation traps

Both produce a disc that looks completely normal:

- **y flip.** Image rows run down, disc-space y runs up. Get it wrong and
  superior and inferior swap, so an inferior notch is modelled superiorly — and
  since the optical inversion flips it again, the simulated field defect comes
  out on the side it started on. Two wrongs, one plausible answer.
- **x mirror.** Import must mirror on the same condition as the renderer
  (`Laterality.MirrorsX`). Get it wrong and nasal and temporal swap, inverting
  ISNT for one eye only.

`PixelBelowTheCentre_IsInferior` and
`PixelRightOfTheCentre_SwapsNasalAndTemporalBetweenEyes` exist solely to catch
these.

> **Privacy.** A fundus photograph is patient data. Nothing here uploads
> anything, but a `FundusStudy` asset carries the image with it into whatever
> repository it is committed to. The shipped teaching cases use synthetic images.

---

## Dropping in an eye model

Put any `.fbx`, `.obj` or `.blend` into **`Assets/Resources/EyeGlobe/`** and run.
Nothing to wire, no scene to regenerate. **Ocular → Eye Model → Open Drop Folder**
opens it; **What Is Installed** says what it found.

Press **G** to cycle **ghost → sectioned → solid → off**. Ghost is the default,
because a solid eye hides the disc you came to look at, and *sectioned* culls the
near faces so the view falls through into the interior.

It does the fiddly parts for you:

| | |
|---|---|
| **Scale** | The mesh is measured and fitted to a 24 mm eye, so the units it was authored in don't matter |
| **Origin** | Centred on its own bounds, so an off-origin export still ends up concentric |
| **Placement** | Globe centre one radius up the optic axis, putting the disc at the posterior pole |
| **Materials** | Replaced with a clinical sclera — a built-in-pipeline model won't render magenta |
| **Colliders** | Stripped |
| **Shadows** | Casting off |

Two of those are load-bearing rather than tidy. A **mesh collider** around the
whole eye eats every raycast the orbit controller and planimetry tool make — the
tools stop responding and the globe looks innocent. And a **shadow** from the
globe across the cup wall is indistinguishable from cup depth, which is the one
cue this view exists to provide.

Sizing uses the model's **largest** axis, not its mean: an eye is nearly
spherical, so a model measuring much longer on one axis has an optic nerve stump
attached, and fitting to the mean would inflate the globe past life size.

If the model's optic axis isn't `+Y` in its own space, set **Model Rotation
Euler** on the `EyeGlobeView` component.

> **It is scenery.** Every number reported is measured off the parametric
> nerve-head surface, never off this mesh — a loose or stylised eye cannot make a
> measurement wrong. For the same reason, never replace the optic nerve head
> itself with an imported mesh: you would be looking at one surface while the
> engine measured a different, invisible one.

On this machine (integrated graphics, WebGL target) decimate to roughly 20k
triangles. The importer warns past 60k.

---

## Loading an eye from inside the simulator

Press **Load Eye Image...** (top right). Pick a fundus photograph and the eye
appears — no rebuild, no Editor, no command line.

```
                        ~1 second                      ~1 minute
pick file ──┬── segment, fit, measure ──▶ numbers on screen
            └── Tripo ──▶ GLB ──▶ glTFast ──▶ eye appears around the disc
```

**The two halves deliberately do not wait for each other.** The fit is local and
instant, so the clinician has disc diameters, a CDR and a notch before they have
finished letting go of the mouse; the generated eye arrives a minute later. One
progress bar with the model first would hold the medicine hostage to the
scenery — and it means an empty Tripo balance, a bad key or no network at all
costs you the decorative globe and nothing else.

### Cropped images are detected and calibrated differently

A full fundus photograph is a bright circle on black, and the camera's "30
degrees" describes **that circle**. Crop into it — as everyone does to look
closer at the disc, and as every phone screenshot does — and the retina now
fills the frame while spanning far less than 30°. Scaling it as a full field
shrinks every millimetre: observed on a real photograph, a normal disc came back
as **1.35 mm and labelled "Small"**, which is a clinical misread, since disc size
is the context a CDR is read against.

So the framing is measured. A frame with under 8% dark surround is treated as
cropped (a circle inscribed in its frame leaves about 21%), and the scale falls
back to an **assumed 1.8 mm disc** — which marks itself circular, so CDR and
rim/disc stay valid while absolute sizes are labelled ASSUMED and disc-size
classification is suppressed. The reason appears in the vignette, next to the
numbers it qualifies.

That is strictly more honest than the alternative: a wrong field angle reports
confident sizes that are simply false.

### When the auto-trace fails, draw it yourself

The cup margin is not a colour boundary — it is where the surface bends, read
off the vessels diving over the edge. On a compressed or low-contrast photograph
the tracer finds no pallor and reports **a cup of zero**, while a clinician
looking at the same picture can see the cup perfectly well.

So trace it: **B** to begin, right-click to place points, **Enter** to close each
contour, then **U** to adopt your margins as the model.

`U` is deliberately explicit and never automatic. Planimetry's normal job is a
teaching exercise scored against the measured truth, and silently promoting the
learner's outline to truth would make them right by definition. The round trip
is untouched either way: traced contours are fitted to a spec, the spec
generates a surface, and the engine measures **that**, so a hand-drawn CDR is
still a measurement of geometry.

### GLB here, FBX in the offline pipeline

Not a preference — a constraint. **Unity's FBX and OBJ importers are
Editor-only**; nothing in a built player can read an `.fbx`. glTF is the only
common format with a runtime loader, and GLB is its single-file binary flavour
with textures embedded, so one download is the whole model.

That inverts the offline pipeline's `quad: true` trick, and it is **cheaper**:
30 credits instead of 35, because the quad add-on is no longer being paid for.

| | Offline (`ocular_watch.py`) | In-app button |
|---|---|---|
| Format | FBX (`quad: true`) | GLB |
| Loader | Editor importer | glTFast at runtime |
| Cost | 35 credits | **30 credits** |
| Rebuild needed | yes | no |

### The API key is never compiled in

A key baked into a player is a key anyone holding that player can extract, and
Tripo keys spend real money. So it is resolved at run time, in order:

1. `TRIPO_API_KEY` environment variable
2. `tripo_key.txt` next to the executable
3. Whatever was typed into the app (PlayerPrefs)

None of these are secret *from the machine's own user* — that is the right
threat model. This stops you distributing your key by accident; it does not
defend against someone already sitting at the computer.

### Scripted import

`-importImage <path>` imports on launch with no clicking — for a demo that opens
on a given eye, and for exercising upload, generation and glTF loading without a
human at the mouse:

```bash
Ocular.exe -importImage "C:\path\to\eye.jpg"
```

It also tells `StudyAutoLoader` to stand down in `Awake`. Both load in `Start`,
Unity does not order `Start` between components, and when the auto-loader won
that race the chosen image was silently replaced by whatever study was baked
into the build. Awake always precedes every Start, which makes it deterministic
rather than a coin toss.

---

## One image in, a running simulation out

Leave the watcher running and **dropping a file is the whole workflow**:

```bash
python Tools/ocular_watch.py
```

Every JPEG or PNG dropped into `Inbox/` is generated, fitted and loaded into a
freshly built simulator, then moved to `Inbox/done/`. The **filename becomes the
patient name**, and `OD`/`OS` anywhere in it picks the eye — `A_Jones_OS.png`
becomes "A Jones OS", left eye. Failures go to `Inbox/done/failed/` rather than
being retried forever.

A drop is picked up only once its size has stopped changing: a 15 MB photograph
arriving over a share exists long before it is complete, and a half-written JPEG
segments into nonsense without ever looking like an error.

Single images, same thing without the folder:

```bash
python Tools/ocular_pipeline.py --image C:\path\to\eye.jpg --name "Patient A" --eye OD --axial-length 24.2
```

Both need `TRIPO_API_KEY`; `--no-tripo` skips generation and fits the photograph
only, `--no-launch` stops before building.

**One patient at a time.** Each run clears the previous study *and* photograph
first. `StudyAutoLoader` opens the alphabetically-last study, which is a fine
tiebreak and a terrible way to mean "the one I just imported" — a leftover
`Smoke_Test` outranks `Patient_A` forever. Clearing also stops the last
patient's fundus image shipping inside a distributable build as orphaned
clinical data, since everything under `Resources` is baked in whether anything
references it or not. The `_fit.txt` reports are the audit trail.

**Why a drop takes a few minutes.** Tripo is about a minute, the import and fit
another thirty seconds, the player rebuild a minute more. The rebuild is
unavoidable for a standalone player because Resources is baked in at build time.
For the fast loop, keep the Editor open and run with `--no-launch`.

### Two models come out of one image, and they are not the same kind of thing

This is the part to understand before trusting anything on screen.

```
                 ┌─ Tripo ──────▶ generated mesh ──▶ EyeGlobeView   SCENERY
fundus photo ────┤
                 └─ segment ─▶ fit ─▶ spec ─▶ OnhSurface ─▶ metrics  MEASURED
```

The **Tripo mesh is generative** — a neural network invented its geometry to be
plausible given the picture. Its depths are not this patient's depths; they are
nobody's. It loads through `EyeGlobeView`, which exists precisely so an imported
mesh can be context and nothing more.

The **measured nerve head is fitted** from the traced margins and regenerated as
a height field the engine samples. Every number reported comes from there.

Letting a generated mesh anywhere near the measurements would hand a clinician
invented numbers wearing the costume of measured ones. Nothing in the pipeline
allows it, and `FundusImportTests` is what keeps it that way.

> A fundus photograph is also a poor Tripo input: a flat circular view of the
> inside of an eye, with no subject to separate from its background, fed to a
> model trained on objects. Expect a disc-shaped lump — fine as context, useless
> as anatomy. Generating one good globe and reusing it across patients is
> usually better than a fresh blob per patient at credit cost, which is what
> `--no-tripo` is for.

### Where things land

| | |
|---|---|
| `Assets/Resources/FundusPhotos/` | the photograph |
| `Assets/Resources/EyeGlobe/` | the Tripo FBX (one at a time; the previous is cleared) |
| `Inbox/` | drop folder watched by `ocular_watch.py`; handled files move to `Inbox/done/` |
| `Assets/Resources/FundusStudies/` | the `FundusStudy` asset and a `_fit.txt` report |
| `pipeline_job.json` | project root, **not** under `Assets` — in `Resources` it would ship inside the player carrying the patient's name |

Studies go to Resources, not the `CaseLibrary`: `BuildCaseLibrary` **clears and
rebuilds** the library from `CasePresets` on every scene regeneration, so a
fitted patient case added there would survive exactly until the next regenerate
and then vanish silently. From Resources, `StudyAutoLoader` picks it up at
startup with no scene regeneration and no rebuild of anything but the player.

### Tripo API

**v3** (`https://openapi.tripo3d.ai/v3`). v2 is retired as of **1 Nov 2026** —
nothing here speaks v2.

| Step | Call |
|---|---|
| Upload | `POST /files`, multipart field `file` → `data.file_token` |
| Generate | `POST /generation/image-to-model`, `{input, model, quad, face_limit}` → `data.task_id` |
| Poll | `GET /tasks/{id}` → `status` of `queued`/`running`/`success`/`failed`/`cancelled`, then `output.model_url` |

`quad: true` is not about topology here — it is the documented way to force the
output format to **FBX**, which Unity imports natively. That removes both a
separate `POST /models/convert` task (more credits, more waiting) and the
glTFast dependency a GLB would need. The script still checks the returned URL
and falls back to a convert task if it is not FBX, because a silently-GLB file
lands as an unimportable asset and the globe just never appears.

Errors come back as HTTP 200 with a non-zero `code` in the body, so the client
checks `code`, not the status line.

### Credits

**A Tripo Studio subscription does not include API credit.** They are bought
separately, and this is the single most surprising thing about getting the
pipeline running — the key authenticates fine, uploads succeed, and task
creation then fails with `code 2010`.

One eye costs **35 credits ($0.35)**: image-to-3D with standard texture (30)
plus the quad add-on (5). The quad flag is the cheaper path, not an
extravagance — without it the output is GLB at 30 credits and then needs a
Format Convert to FBX, billed at the Advanced rate (10) because this script
passes non-default `texture_size` and `texture_format`. 40 credits and two
round trips, versus 35 and one.

Top up from $1 at [developers.tripo3d.ai/en/pricing](https://developers.tripo3d.ai/en/pricing)
(100 credits = $1.00). Failed and cancelled tasks are not charged.

The pipeline checks the balance **before** uploading, because the API accepts
the file and only rejects at task creation — without the preflight you wait
through an upload to be told the account was empty before you started. An empty
account is not fatal: generation is skipped and the nerve head is still fitted
and measured, since no measurement depends on the globe.

---

## The cases

Seven presets, chosen so that **cup-to-disc ratio alone cannot get you through them**:

1. **Healthy average disc** — the reference point
2. **Large physiologic cup** — *false positive.* CDR 0.65 and completely normal: a large
   disc carries a large cup on a normal amount of rim
3. **Ocular hypertension** — *false positive.* 28 mmHg, healthy nerve, thick cornea
   inflating the reading
4. **Early POAG** — modest CDR but the rim ordering is wrong
5. **Normal-tension glaucoma** — *false negative.* Normal pressure, inferior notch, disc
   haemorrhage, thin cornea
6. **Moderate POAG** — both poles, peripapillary atrophy
7. **Advanced POAG** — bean-pot cupping, RNFL at its floor

A case list of only obvious glaucoma teaches a learner to over-call every large cup they
ever see, which is the mistake that actually happens in clinic.

---

## Two traps worth knowing about

**DDLS takes rim width over disc *diameter*, not radius.** Dividing by the radius doubles
every ratio and stages a case about two bands too favourably — an eye the printed chart
calls stage 3 reports as stage 1. Sanity anchor: a healthy disc at CDR 0.3 gives
`(1 − 0.3) / 2 = 0.35`, i.e. DDLS stage 2, which is where a normal disc belongs.

**The optical inversion.** Light from the superior field lands on the inferior retina,
whose fibres enter the disc inferiorly — so an **inferior** rim notch produces a
**superior** arcuate scotoma. It is one negation in
`VisualFieldModel.FieldPointToDiscAngle`, it is the easiest thing here to get backwards,
and backwards output looks entirely plausible. `ClinicalModelTests` asserts the direction
end to end.

---

## VR and AR

Unity ships no first-party WebXR, so this uses **`com.de-panther.webxr` 0.25.0** from
OpenUPM (a scoped registry entry is in `Packages/manifest.json`). One WebGL build serves
all three modes.

```bash
"C:\Program Files\Unity\Hub\Editor\6000.0.25f1\Editor\Unity.exe" -batchmode -quit -projectPath "D:\Games\Ocular" -executeMethod Ocular.Editor.OcularBuildPipeline.BuildWebGL -logFile -
```

**WebXR needs three things, and each fails silently on its own:**

1. The **WebGL template** must be `WebXRFullView2020` — the default Unity template has no
   WebXR JavaScript in it, so no session can ever start. Templates live in the package's
   `Hidden~` folder and must be copied into `Assets/WebGLTemplates`.
2. The **WebXR loader must be assigned** to the WebGL target in XR Plug-in Management.
   Registered is not the same as enabled — unassigned, `activeLoader` is null at runtime
   and `isSupportedVR` is false forever.
3. It must be served over **HTTPS** (or `localhost`). Browsers refuse WebXR on plain HTTP.

`OcularBuildPipeline.BuildWebGL` does 1 and 2 automatically via `OcularXrSetup`.

### How the three modes differ

They are not one scene at different scales:

| Mode | Scale | Camera | Notes |
|---|---|---|---|
| Desktop | ~120× | Orbit controller | Disc as a specimen on a turntable |
| VR | ~90× | Headset pose | Orbit is **disabled** — two things writing the camera pose is instant motion sickness |
| AR | ~45× | Headset pose | Camera clears to **alpha 0** or the passthrough feed is painted over |

Entering VR/AR is done with the on-screen **buttons**, not keyboard shortcuts: browsers
only start an XR session from a genuine user gesture, and they refuse silently.

The interaction model is shared through `IPointerSource`. `XrPointer` falls back to the
mouse when no session is running, so the VR interaction path is testable on a desktop
without a headset — otherwise it would be the least-tested code in the project.

## Not yet built

- Parameter sliders, TSNIT and visual-field plots (the engine computes all of it; only the
  metrics panel is wired to display)
- On-screen provenance badges — the fit's caveats reach the HUD through the teaching note,
  but an `ASSUMED` disc diameter deserves a marker next to the number itself
- Runtime photograph upload. The whole imaging pipeline is plain C# and runs in a player;
  only the import *window* is editor-only, so a WebGL file picker is the missing piece
- On-canvas contour editing. Margins are correctable as `Vector2[]` in the inspector, which
  is honest but not a tool anyone wants to trace a cup with
- Cross-section clip plane. The eye globe now sections by face culling, which works but is
  cruder than a real clip plane through both globe and disc
- Two-handed grab/scale in VR
