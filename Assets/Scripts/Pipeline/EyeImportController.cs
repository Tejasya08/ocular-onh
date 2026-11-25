// -----------------------------------------------------------------------------
// Ocular -- pick an image inside the running simulator and get an eye.
//
// THE ORDER HERE IS THE DESIGN. Two things happen to one image, and they are
// deliberately not made to wait for each other:
//
//   1. The fit runs IMMEDIATELY and locally. Segment, fit, rebuild the surface,
//      measure. About a second, no network. The clinician has disc diameters, a
//      CDR and a notch before they have finished letting go of the mouse.
//
//   2. Tripo runs in the BACKGROUND, taking a minute or so, and the generated
//      eye appears around the disc whenever it arrives.
//
// Doing it the other way round -- one progress bar, model first, measurements
// at the end -- would make a clinical tool feel like a download, and would hold
// the medicine hostage to the scenery. It also means a Tripo failure, an empty
// balance or no network at all costs you the decorative globe and nothing else.
// The whole measurement path is pure C# and never touches the network.
//
// Everything is wrapped: a failure anywhere in step 2 is reported into the HUD
// and leaves the fitted eye standing.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using Ocular.Anatomy;
using Ocular.Core.Anatomy;
using Ocular.Core.Diagnostics;
using Ocular.Core.Imaging;
using Ocular.Data;

namespace Ocular.Pipeline
{
    [RequireComponent(typeof(OnhModelBinding))]
    public sealed class EyeImportController : MonoBehaviour
    {
        [Header("Capture assumptions")]
        [Tooltip("Fundus camera field angle. 30 and 45 are the usual settings.")]
        [SerializeField] private float _fieldAngleDeg = 30f;

        [Tooltip("Axial length, mm. Longer in myopia, and it scales every reported size.")]
        [SerializeField] private float _axialLengthMm = 24f;

        [SerializeField] private Laterality _laterality = Laterality.OD;

        [Header("Generation")]
        [Tooltip("Send the image to Tripo for a 3D eye. Off = measure only, no credit spent.")]
        [SerializeField] private bool _generateModel = true;

        private OnhModelBinding _binding;
        private EyeGlobeView _globe;
        private CancellationTokenSource _cancellation;
        private string _autoImportPath;
        private bool _lastCalibrationWasCircular;
        private string _lastImagePath;
        private string _cupWarning;

        /// <summary>Human-readable state for the HUD.</summary>
        public string Status { get; private set; }

        /// <summary>0..1 for the generation bar. Negative when nothing is running.</summary>
        public float Progress { get; private set; }

        public bool Busy { get; private set; }

        /// <summary>The fit of the most recently imported eye, if any.</summary>
        public OnhFitReport LastReport { get; private set; }

        /// <summary>Raised whenever Status, Progress or Busy changes.</summary>
        public event Action<EyeImportController> Changed;

        public Laterality Laterality
        {
            get { return _laterality; }
            set { _laterality = value; Raise(); }
        }

        /// <summary>Field angles offered by the cycle button. The common camera settings.</summary>
        private static readonly float[] FieldAngles = { 30f, 45f, 50f, 60f };

        public float FieldAngleDeg { get { return _fieldAngleDeg; } }

        /// <summary>
        /// Steps to the next camera field angle and re-fits the current image.
        ///
        /// <para>This has to be reachable from the UI, because it is not a
        /// refinement -- it sets the entire scale. The same photograph read as 30
        /// degrees or as 45 gives disc diameters that differ by half: a 45-degree
        /// image (disc AND macula both in frame, which is the giveaway) scored as
        /// 30 reports a perfectly normal disc as 1.35 mm and labels it "Small".
        /// Disc size is the context a CDR is read against, so that is a clinical
        /// misread and not a cosmetic one, and only the person who took the
        /// photograph knows which setting the camera was on.</para>
        /// </summary>
        public void CycleFieldAngle()
        {
            int index = 0;

            for (int i = 0; i < FieldAngles.Length; i++)
                if (Mathf.Approximately(FieldAngles[i], _fieldAngleDeg)) { index = i; break; }

            _fieldAngleDeg = FieldAngles[(index + 1) % FieldAngles.Length];

            if (!string.IsNullOrEmpty(_lastImagePath) && !Busy)
            {
                // Re-fit rather than merely remember: the number on the button
                // would otherwise disagree with the numbers on screen.
                ReimportLast();
            }
            else
            {
                SetStatus("Field angle " + _fieldAngleDeg.ToString("0") + " deg.", -1f);
            }
        }

        private async void ReimportLast()
        {
            string path = _lastImagePath;

            if (string.IsNullOrEmpty(path) || Busy) return;

            Busy = true;

            try
            {
                // Measurement only. Re-running Tripo would spend credits to
                // regenerate scenery that the field angle does not affect.
                await MeasureAsync(path);

                SetStatus("Re-fitted at " + _fieldAngleDeg.ToString("0") + " deg field.", -1f);
            }
            catch (Exception ex)
            {
                SetStatus("Re-fit failed: " + ex.Message, -1f);
            }
            finally
            {
                Busy = false;
                Raise();
            }
        }

        public bool GenerateModel
        {
            get { return _generateModel; }
            set { _generateModel = value; Raise(); }
        }

        public bool CanPickFile { get { return RuntimeFileDialog.Supported(); } }

        private void Awake()
        {
            _binding = GetComponent<OnhModelBinding>();
            _globe = GetComponent<EyeGlobeView>();

            _autoImportPath = ReadCommandLineArgument("-importImage");

            // Claim the eye NOW, in Awake, before any Start runs. StudyAutoLoader
            // also loads in Start, Unity does not order Start between components,
            // and when it won the race the image the user actually asked for was
            // silently replaced by whatever study was baked into the build.
            if (!string.IsNullOrEmpty(_autoImportPath))
            {
                StudyAutoLoader loader = GetComponent<StudyAutoLoader>();
                if (loader != null) loader.SuppressAutoLoad();
            }

            Progress = -1f;
            Status = RuntimeFileDialog.Supported()
                ? "Load an eye image to begin."
                : "No file picker on this platform -- use the Inbox folder.";
        }

        private void Start()
        {
            // Path was read in Awake so StudyAutoLoader could be told to stand
            // down before either Start ran; the import itself happens here.
            if (!string.IsNullOrEmpty(_autoImportPath))
            {
                OLog.Info(LogCat.UI, "-importImage " + _autoImportPath);
                ImportFile(_autoImportPath);
            }
        }

        private void OnDestroy()
        {
            if (_cancellation != null) _cancellation.Cancel();
        }

        private static string ReadCommandLineArgument(string name)
        {
            try
            {
                string[] args = Environment.GetCommandLineArgs();

                for (int i = 0; i < args.Length - 1; i++)
                    if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                        return args[i + 1];
            }
            catch (Exception)
            {
                // Some platforms restrict this. Not worth failing a launch over.
            }

            return null;
        }

        /// <summary>Opens the OS file picker and imports whatever is chosen.</summary>
        public void PickAndImport()
        {
            if (Busy) return;

            string path = RuntimeFileDialog.PickImage("Choose a fundus photograph");

            if (string.IsNullOrEmpty(path)) return;   // cancelled

            ImportFile(path);
        }

        /// <summary>
        /// Downloads an image from a URL and imports it through the same pipeline
        /// as the manual button. Called by the WebGL host page via SendMessage when
        /// a ?image= query parameter is present.
        ///
        /// <para>This is the entry point for the hosted-service workflow:
        ///   https://&lt;host&gt;/?image=&lt;signed-url&gt;
        /// The page script reads the parameter and calls this method after Unity
        /// initialises. Everything that follows is identical to picking a local
        /// file: segment, fit, measure, then generate the 3D eye via Tripo.</para>
        ///
        /// <para>The URL is typically a Supabase signed URL or any publicly
        /// readable HTTPS URL. The download runs on the main thread via
        /// UnityWebRequest so it integrates cleanly with Unity's async model and
        /// WebGL's single-threaded runtime.</para>
        /// </summary>
        public async void LoadFromUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                SetStatus("LoadFromUrl: empty URL.", -1f);
                return;
            }

            if (Busy)
            {
                SetStatus("Busy -- already importing an image.", -1f);
                return;
            }

            Busy = true;
            LastReport = null;

            try
            {
                SetStatus("Downloading image...", 0f);

                byte[] imageBytes = await DownloadBytesAsync(url);

                if (imageBytes == null || imageBytes.Length == 0)
                    throw new InvalidDataException("The image downloaded from the URL was empty.");

                // Write to a temp path so the existing ImportAsync / MeasureAsync
                // path (which reads from disk) can be reused without change.
                // On WebGL Application.temporaryCachePath is an in-memory virtual
                // FS; on desktop it is a real temp directory.
                string fileName = System.Uri.UnescapeDataString(
                    System.IO.Path.GetFileName(new System.Uri(url).AbsolutePath));

                if (string.IsNullOrEmpty(System.IO.Path.GetExtension(fileName)))
                    fileName += ".jpg";

                string tempPath = System.IO.Path.Combine(
                    Application.temporaryCachePath, fileName);

                System.IO.File.WriteAllBytes(tempPath, imageBytes);

                await ImportAsync(tempPath);
            }
            catch (OperationCanceledException)
            {
                SetStatus("Cancelled.", -1f);
            }
            catch (Exception ex)
            {
                OLog.Error(LogCat.UI, "LoadFromUrl failed: " + ex);
                SetStatus("URL import failed: " + ex.Message, -1f);
            }
            finally
            {
                Busy = false;
                Raise();
            }
        }

        /// <summary>
        /// Downloads raw bytes from a URL using UnityWebRequest.
        /// Works on WebGL (no Thread.Sleep, no blocking IO).
        /// </summary>
        private async Task<byte[]> DownloadBytesAsync(string url)
        {
            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                // No auth header: the URL is expected to be pre-signed (Supabase
                // signed URL) or publicly readable. Adding a credential here would
                // cause CDN-side pre-signed URLs to be rejected as double-signed.
                request.timeout = 60;

                UnityWebRequestAsyncOperation op = request.SendWebRequest();

                while (!op.isDone)
                    await Task.Yield();

                if (request.result != UnityWebRequest.Result.Success)
                    throw new InvalidOperationException(
                        "Failed to download image: " + request.error + " (" + url + ")");

                return request.downloadHandler.data;
            }
        }

        /// <summary>Imports a specific file. Safe to call from a drop handler.</summary>
        public async void ImportFile(string path)
        {
            if (Busy) return;

            Busy = true;
            LastReport = null;

            try
            {
                await ImportAsync(path);
            }
            catch (OperationCanceledException)
            {
                SetStatus("Cancelled.", -1f);
            }
            catch (Exception ex)
            {
                // async void cannot propagate anywhere useful, so nothing may
                // escape: an unhandled exception here would be swallowed by the
                // runtime and leave the button permanently busy.
                OLog.Error(LogCat.UI, "Import failed: " + ex);
                SetStatus("Import failed: " + ex.Message, -1f);
            }
            finally
            {
                Busy = false;
                Raise();
            }
        }

        private async Task ImportAsync(string path)
        {
            // --- Step 1: measure. Local, immediate. ----------------------------
            byte[] bytes = await MeasureAsync(path);

            // --- Step 2: the eye around it. Slow, optional, non-blocking. ------
            if (!_generateModel)
            {
                SetStatus("Measured. 3D generation is off.", -1f);
                return;
            }

            string apiKey = TripoCredentials.Resolve();

            // A key is only needed to BUY a model. One already on disk should
            // load on a machine that has no key and no internet at all, which is
            // what a clinic terminal usually looks like.
            if (string.IsNullOrEmpty(apiKey) && !File.Exists(CachePathFor(Path.GetFileName(path))))
            {
                SetStatus("Measured. No Tripo key -- set TRIPO_API_KEY or put "
                          + TripoCredentials.KeyFileName + " beside the app.", -1f);
                return;
            }

            await GenerateAsync(apiKey, bytes, Path.GetFileName(path));
        }

        /// <summary>
        /// Reads, traces, fits and loads one image. Returns its bytes so the
        /// caller can hand the same ones to Tripo without a second read.
        ///
        /// <para>Separate from ImportAsync so that changing the field angle can
        /// re-measure WITHOUT re-generating: the camera field sets the scale and
        /// has no effect whatsoever on the shape of the mesh Tripo returns, so
        /// spending credits again on an identical model would be paying for
        /// nothing.</para>
        /// </summary>
        private async Task<byte[]> MeasureAsync(string path)
        {
            SetStatus("Reading image...", -1f);

            byte[] bytes = File.ReadAllBytes(path);

            Texture2D photo = new Texture2D(2, 2, TextureFormat.RGBA32, false);

            // LoadImage always produces a readable texture, which is what the
            // imaging code needs and what an asset-imported texture lacks unless
            // Read/Write was ticked.
            if (!photo.LoadImage(bytes))
                throw new InvalidDataException("That file is not a PNG or JPEG Unity can read.");

            photo.name = Path.GetFileNameWithoutExtension(path);

            SetStatus("Tracing disc and cup...", -1f);

            // Yields once so the status above actually paints before the trace
            // blocks the main thread for a moment on a large photograph.
            await Task.Yield();

            FundusStudy study = BuildStudy(photo, path);

            LastReport = _binding.LoadStudy(study);

            if (LastReport == null)
                throw new InvalidOperationException("The disc could not be traced in that image.");

            _lastImagePath = path;

            OLog.Info(LogCat.UI, "Imported " + photo.name + " at "
                + _fieldAngleDeg.ToString("0") + " deg -- " + LastReport.Summary());

            // A cup this small is not a shallow cup, it is a FAILED TRACE -- and
            // it has a visible consequence people reasonably misread as a broken
            // program: with no cup there is no excavation, so the nerve head
            // renders as a flat disc and the whole view looks two-dimensional.
            // Saying so turns "this 3D viewer is showing me a 2D picture" into an
            // instruction the user can act on.
            // An unconfirmed cup must never read as a measured one. Saying so
            // here, in the status line, is the difference between a clinician
            // correcting a contour and a clinician quoting it.
            if (_cupWarning != null)
            {
                SetStatus("Cup is UNCONFIRMED -- trace it yourself: B, right-click, Enter, then U.", -1f);
            }

            if (LastReport.Spec.CupToDiscVertical < FlatDiscCdr)
            {
                SetStatus("No cup found -- the disc will look FLAT. Trace it yourself: "
                          + "B, right-click points, Enter, then U.", -1f);

                OLog.Warn(LogCat.UI, "Cup trace collapsed (CDR "
                    + LastReport.Spec.CupToDiscVertical.ToString("0.00")
                    + "). The surface has no excavation and will render flat.");
            }

            return bytes;
        }

        /// <summary>
        /// Where generated models are kept, beside the app.
        ///
        /// <para>Generation costs real money per image and is not deterministic,
        /// so re-importing the same photograph must not re-buy the same eye. The
        /// cache is also what lets a model be prepared in advance and simply
        /// dropped in -- useful for a demo, and for a clinic machine with no
        /// outbound internet.</para>
        /// </summary>
        public static string CacheDirectory()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "../GeneratedModels"));
        }

        private static string CachePathFor(string imageFileName)
        {
            string stem = Path.GetFileNameWithoutExtension(imageFileName);

            foreach (char c in Path.GetInvalidFileNameChars())
                stem = stem.Replace(c, '_');

            return Path.Combine(CacheDirectory(), stem + ".glb");
        }

        private async Task GenerateAsync(string apiKey, byte[] imageBytes, string fileName)
        {
            if (_cancellation != null) _cancellation.Cancel();
            _cancellation = new CancellationTokenSource();

            CancellationToken ct = _cancellation.Token;

            try
            {
                byte[] glb;

                string cached = CachePathFor(fileName);

                if (File.Exists(cached))
                {
                    SetStatus("Loading saved model...", 0.9f);
                    glb = File.ReadAllBytes(cached);

                    OLog.Info(LogCat.UI, "Reusing cached model " + cached
                        + " (" + (glb.Length / 1048576f).ToString("0.0") + " MB) -- no credits spent.");
                }
                else
                {
                    // Reached only when there was no cached model; the caller has
                    // already established a key exists in that case.
                    TripoRuntimeClient client = new TripoRuntimeClient(apiKey);

                    glb = await client.GenerateModelAsync(
                        imageBytes, fileName,
                        (label, fraction) => SetStatus(label + "...", fraction),
                        ct);

                    TrySaveToCache(cached, glb);
                }

                SetStatus("Loading model...", 0.95f);

                await LoadGlbAsync(glb, ct);

                SetStatus("Done. Press G to show the eye.", -1f);
            }
            catch (TripoException ex)
            {
                // The measurements are already on screen and stay there. Only the
                // scenery was lost, and the message says which.
                string message = ex.Message;

                // A 401 is almost never a Tripo problem -- it is the wrong key
                // reaching it, and the user cannot see which of three sources
                // supplied it. Saying so beats "Unauthorized" by a mile.
                if (message.Contains("401"))
                {
                    message = "key rejected (" + TripoCredentials.Describe(apiKey)
                            + "). Set TRIPO_API_KEY to your real key, or put it in "
                            + TripoCredentials.KeyFileName + " beside the app.";
                }

                SetStatus("Measured. 3D model failed: " + message, -1f);
                OLog.Warn(LogCat.UI, "Tripo generation failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Writes a freshly generated model to the cache.
        ///
        /// <para>Never fatal. The model is already in memory and about to be
        /// displayed, so a read-only install directory or a full disk should cost
        /// the user the saving, not the eye they just paid for.</para>
        /// </summary>
        private static void TrySaveToCache(string path, byte[] glb)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, glb);

                OLog.Info(LogCat.UI, "Saved model to " + path
                    + " -- re-importing this image will not spend credits again.");
            }
            catch (Exception ex)
            {
                OLog.Warn(LogCat.UI, "Could not cache the model: " + ex.Message);
            }
        }

        private async Task LoadGlbAsync(byte[] glb, CancellationToken ct)
        {
            if (glb == null || glb.Length == 0)
                throw new TripoException("The downloaded model was empty.");

            GLTFast.GltfImport import = new GLTFast.GltfImport();

            bool loaded = await import.LoadGltfBinary(glb, null, null, ct);

            if (!loaded) throw new TripoException("The downloaded model could not be read as glTF.");

            GameObject holder = new GameObject("TripoEye");

            bool instantiated = await import.InstantiateMainSceneAsync(holder.transform, ct);

            if (!instantiated)
            {
                Destroy(holder);
                throw new TripoException("The model loaded but could not be instantiated.");
            }

            if (_globe != null)
            {
                _globe.AdoptModel(holder);
            }
            else
            {
                Destroy(holder);
                OLog.Warn(LogCat.UI, "No EyeGlobeView on this object; the model was discarded.");
            }
        }

        /// <summary>
        /// Builds a study from the loaded photograph: calibrate, segment, trace.
        /// The fit itself happens in FundusStudy.Fit, called by LoadStudy, so the
        /// numbers are still measured off generated geometry and not off this.
        /// </summary>
        private FundusStudy BuildStudy(Texture2D photo, string path)
        {
            FundusImage image = FundusPhotoTexture.ToFundusImage(photo);

            if (image == null)
                throw new InvalidDataException("That image could not be read into memory.");

            FundusFraming framing = image.MeasureFraming();
            OLog.Info(LogCat.UI, framing.Explain());

            ImageCalibration calibration;
            SegmentationResult segmentation;

            if (!framing.LooksCropped)
            {
                calibration = ImageCalibration.FromCameraField(
                    _fieldAngleDeg, framing.FieldWidthPixels, _axialLengthMm);

                segmentation = Trace(image, calibration);
            }
            else
            {
                // Two passes, because the honest calibration for a cropped image
                // depends on finding the disc, and finding the disc needs a search
                // radius in pixels, which depends on a calibration.
                //
                // Pass 1 uses a deliberately crude scale derived from the frame
                // size -- only to locate the disc. Pass 2 re-traces with the real
                // one. Nothing from pass 1 is reported.
                ImageCalibration provisional = ImageCalibration.FromAssumedDiscDiameter(
                    Mathf.Min(image.Width, image.Height) * 0.25f, AssumedDiscDiameterMm);

                SegmentationResult rough = Trace(image, provisional);

                float discPixels = provisional.MmToPixels(rough.Disc.VerticalDiameterMm);

                calibration = ImageCalibration.FromAssumedDiscDiameter(
                    discPixels, AssumedDiscDiameterMm);

                segmentation = Trace(image, calibration);
            }

            float fieldPixels = framing.FieldWidthPixels;
            _lastCalibrationWasCircular = calibration.IsCircular;

            FundusStudy study = ScriptableObject.CreateInstance<FundusStudy>();

            study.DisplayName = Path.GetFileNameWithoutExtension(path);
            study.Laterality = _laterality;
            study.Photograph = photo;

            // The study must record the calibration that was actually used, not
            // the one the inspector happens to show. Writing CameraField here for
            // a cropped image would let FundusStudy.Fit rebuild a different,
            // wrong scale and quietly undo the whole correction.
            if (framing.LooksCropped)
            {
                study.ScaleSource = ScaleSource.AssumedDiscDiameter;
                study.AssumedDiscDiameterMm = AssumedDiscDiameterMm;
                study.AssumedDiscDiameterPixels = calibration.MmToPixels(AssumedDiscDiameterMm);
            }
            else
            {
                study.ScaleSource = ScaleSource.CameraField;
            }

            study.FieldAngleDeg = _fieldAngleDeg;
            study.FieldPixels = fieldPixels;
            study.AxialLengthMm = _axialLengthMm;

            study.DiscCentrePixels = segmentation.CentrePixels;
            study.DiscMargin = segmentation.Disc.ToPolygonPixels(calibration, _laterality);
            study.CupMargin = segmentation.Cup.ToPolygonPixels(calibration, _laterality);

            // No OCT at run time, so depth is a population estimate and every
            // surface that shows it is expected to say ASSUMED.
            study.DepthProvenance = DepthProvenance.PopulationAssumed;

            // Straight into the vignette so it sits in the readout beside the
            // numbers it qualifies. A caveat the reader has to go looking for is
            // a caveat that does not exist.
            study.Vignette = framing.Explain();

            _cupWarning = segmentation.CupIsReliable ? null : segmentation.Note;

            if (_cupWarning != null)
            {
                OLog.Warn(LogCat.UI, segmentation.Note);
                study.Vignette += "\n\n" + segmentation.Note;
            }

            return study;
        }

        /// <summary>Population mean vertical disc diameter, used when scale must be assumed.</summary>
        private const float AssumedDiscDiameterMm = 1.8f;

        /// <summary>
        /// Below this CDR the trace has failed rather than found a shallow cup.
        ///
        /// <para>A real disc with no cup at all does exist, but a measured 0.01
        /// means the ray walk terminated at the first dark pixel, which on a
        /// fundus photograph is the vessel trunk emerging from the cup itself.
        /// </para>
        /// </summary>
        private const float FlatDiscCdr = 0.05f;

        /// <summary>
        /// Replaces the automatic trace with margins the clinician drew by hand,
        /// and re-fits from those.
        ///
        /// <para>This is the answer to the failure mode the auto-trace cannot fix
        /// on its own. The cup margin is not a colour boundary -- it is where the
        /// surface bends, read off the vessels as they dive over the edge -- so on
        /// a photograph with weak pallor the tracer finds nothing and reports a
        /// cup of zero. A clinician looking at the same picture can see the cup
        /// perfectly well. Letting them draw it turns an unusable image into a
        /// usable one.</para>
        ///
        /// <para>Deliberately an explicit action rather than automatic. The
        /// planimetry tool's normal job is a TEACHING exercise -- draw the
        /// margins, then compare against the measured truth -- and silently
        /// adopting the learner's trace as truth would destroy the thing being
        /// taught by making them right by definition.</para>
        ///
        /// <para>The round trip is untouched: these contours are fitted to a spec,
        /// the spec generates a surface, and the engine measures that surface. A
        /// hand-drawn CDR is still a measurement of geometry.</para>
        /// </summary>
        public bool ApplyTracedContours(IList<Vector2> discPointsMm, IList<Vector2> cupPointsMm)
        {
            if (discPointsMm == null || cupPointsMm == null
                || discPointsMm.Count < 3 || cupPointsMm.Count < 3)
            {
                SetStatus("Trace both margins first (B to begin, Enter to close each).", -1f);
                return false;
            }

            try
            {
                OnhFitInputs inputs = new OnhFitInputs();

                inputs.Disc = PolarContour.FromDiscSpaceMm(discPointsMm).Smoothed(5);
                inputs.Cup = PolarContour.FromDiscSpaceMm(cupPointsMm).Smoothed(5);

                // Provenance is a property of the IMAGE, not of who drew the
                // outline, so both flags survive a hand trace: a cropped photo is
                // still unscaled, and a photograph still contains no depth.
                inputs.CalibrationIsCircular = _lastCalibrationWasCircular;
                inputs.DepthProvenance = DepthProvenance.PopulationAssumed;

                OnhFitReport report = OnhSpecFitter.Fit(inputs);

                LastReport = report;
                _binding.Spec = report.Spec;

                SetStatus("Using your traced margins. " + report.Summary(), -1f);
                OLog.Info(LogCat.UI, "Adopted hand-traced margins -- " + report.Summary());

                return true;
            }
            catch (Exception ex)
            {
                SetStatus("Could not use that trace: " + ex.Message, -1f);
                OLog.Warn(LogCat.UI, "Hand-traced fit failed: " + ex);
                return false;
            }
        }

        /// <summary>Seed, search radius and trace for one calibration.</summary>
        private SegmentationResult Trace(FundusImage image, ImageCalibration calibration)
        {
            float searchRadiusPx = calibration.MmToPixels(2.2f);

            Vector2 seed = DiscSegmenter.FindDiscSeed(
                image, Mathf.Max(4, Mathf.RoundToInt(searchRadiusPx * 0.5f)));

            return DiscSegmenter.Segment(image, seed, searchRadiusPx, calibration, _laterality);
        }

        private void SetStatus(string status, float progress)
        {
            Status = status;
            Progress = progress;
            Raise();
        }

        private void Raise()
        {
            Action<EyeImportController> handler = Changed;
            if (handler != null) handler(this);
        }
    }
}
