// -----------------------------------------------------------------------------
// Ocular -- the on-screen readout.
//
// Builds its own UI in Awake rather than being wired up in the scene. The scene
// is generated from code, and wiring a dozen Text references through
// SerializedObject is both fragile and unreadable; a HUD that constructs itself
// cannot get half-connected.
//
// Deliberately NOT TextMeshPro. TMP needs its Essentials package imported into
// the project before any text renders, and a batch-generated project does not
// have it -- the failure mode is a build where every label is invisible.
// LegacyRuntime.ttf is built into the engine and always present.
//
// Measurement runs on a 720-meridian sweep with a bisection per meridian, so it
// is recomputed on demand (geometry rebuild, case change, method toggle) rather
// than every frame.
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using Ocular.Anatomy;
using Ocular.Core.Anatomy;
using Ocular.Core.Measurement;
using Ocular.Data;
using Ocular.Interaction;
using Ocular.Pipeline;

namespace Ocular.UI
{
    public sealed class OcularHud : MonoBehaviour
    {
        [SerializeField] private OnhModelBinding _model;
        [SerializeField] private CaseLibrary _library;
        [SerializeField] private OrbitInspector _camera;
        [SerializeField] private PlanimetryTool _planimetry;
        [SerializeField] private XrSessionController _xr;
        [SerializeField] private EyeImportController _import;

        private Text _metrics;
        private Text _caseHeader;
        private Text _controls;
        private Text _planimetryText;
        private Text _xrStatus;
        private Button _vrButton;
        private Button _arButton;
        private Button _importButton;
        private Button _fieldButton;
        private Text _importStatus;

        private MeasurementMethod _method = MeasurementMethod.SlopeContour;
        private int _caseIndex;
        private bool _showClinical = true;

        private static readonly Color PanelColour = new Color(0.05f, 0.06f, 0.09f, 0.82f);
        private static readonly Color Good = new Color(0.55f, 0.85f, 0.60f);
        private static readonly Color Warn = new Color(0.95f, 0.78f, 0.42f);
        private static readonly Color Bad = new Color(0.95f, 0.52f, 0.48f);

        private void Awake()
        {
            BuildUi();
        }

        private void OnEnable()
        {
            if (_model != null) _model.GeometryRebuilt += OnGeometryRebuilt;
            if (_import != null) _import.Changed += OnImportChanged;

            if (_planimetry != null)
            {
                _planimetry.Changed += OnPlanimetryChanged;
                _planimetry.Completed += OnPlanimetryCompleted;
            }
        }

        private void OnDisable()
        {
            if (_model != null) _model.GeometryRebuilt -= OnGeometryRebuilt;
            if (_import != null) _import.Changed -= OnImportChanged;

            if (_planimetry != null)
            {
                _planimetry.Changed -= OnPlanimetryChanged;
                _planimetry.Completed -= OnPlanimetryCompleted;
            }
        }

        private void OnPlanimetryChanged(PlanimetryTool tool) { RefreshPlanimetry(); }

        private void OnPlanimetryCompleted(PlanimetryResult result) { RefreshPlanimetry(); }

        private void Start()
        {
            if (_model != null && _library != null && _library.Count > 0)
            {
                LoadCase(0);
            }
            else
            {
                Refresh();
            }

            RefreshPlanimetry();
        }

        private void OnGeometryRebuilt(OnhModelBinding binding)
        {
            Refresh();
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.N)) LoadCase(_caseIndex + 1);
            if (Input.GetKeyDown(KeyCode.P)) LoadCase(_caseIndex - 1);

            if (Input.GetKeyDown(KeyCode.M))
            {
                _method = _method == MeasurementMethod.SlopeContour
                    ? MeasurementMethod.ReferencePlane
                    : MeasurementMethod.SlopeContour;
                Refresh();
            }

            if (Input.GetKeyDown(KeyCode.C))
            {
                _showClinical = !_showClinical;
                Refresh();
            }

            if (_planimetry != null)
            {
                if (Input.GetKeyDown(KeyCode.B)) _planimetry.Begin();
                if (Input.GetKeyDown(KeyCode.X)) _planimetry.Clear();

                // Adopt the hand trace as the model. Explicit, never automatic:
                // planimetry's normal job is a teaching exercise scored against
                // the measured truth, and silently promoting the learner's
                // outline to truth would make them right by definition.
                if (Input.GetKeyDown(KeyCode.U) && _import != null)
                {
                    _import.ApplyTracedContours(_planimetry.DiscPoints, _planimetry.CupPoints);
                    Refresh();
                }
            }
        }

        private void LoadCase(int index)
        {
            if (_model == null || _library == null || _library.Count == 0) return;

            _caseIndex = ((index % _library.Count) + _library.Count) % _library.Count;

            // Contours belong to the disc they were drawn on. Carrying them into the
            // next case would score the user's margin against a completely different
            // nerve head and report a confident, meaningless error.
            if (_planimetry != null) _planimetry.Clear();

            // LoadCase rebuilds geometry, which raises GeometryRebuilt, which calls
            // Refresh. No explicit refresh needed here.
            _model.LoadCase(_library.At(_caseIndex));
        }

        private void Refresh()
        {
            if (_model == null || _metrics == null) return;

            GlaucomaCase c = _model.Case;

            if (_caseHeader != null)
            {
                _caseHeader.text = c != null
                    ? c.DisplayName + "    (" + _model.Laterality + ", age " + c.PatientAge + ")"
                    : "No case loaded";
            }

            OnhMetrics m = _model.Measure(_method);
            _metrics.text = BuildReport(m, c);
        }

        private string BuildReport(OnhMetrics m, GlaucomaCase c)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(1200);

            sb.Append(m.ToReport());

            if (!_showClinical || c == null) return sb.ToString();

            sb.Append('\n');

            // Colour alone is never the only channel: a meaningful fraction of
            // ophthalmologists are colour-blind, and this is a tool for reading
            // subtle tissue colour in the first place.
            sb.Append("ISNT      ").Append(m.IsntSatisfied ? "OBEYED" : "BROKEN").Append('\n');

            RnflProfile rnfl = _model.MeasureRnfl();
            sb.Append("RNFL      avg ").Append(rnfl.AverageUm.ToString("0")).Append(" um   ")
              .Append(rnfl.Classify()).Append('\n');
            sb.Append("          I ").Append(rnfl.QuadrantUm(DiscSector.Inferior).ToString("0"))
              .Append("  S ").Append(rnfl.QuadrantUm(DiscSector.Superior).ToString("0"))
              .Append("  N ").Append(rnfl.QuadrantUm(DiscSector.Nasal).ToString("0"))
              .Append("  T ").Append(rnfl.QuadrantUm(DiscSector.Temporal).ToString("0"))
              .Append(" um\n");

            VisualFieldResult field = _model.AnalyseField();
            sb.Append("Field     MD ").Append(field.MeanDeviationDb.ToString("0.0"))
              .Append(" dB   PSD ").Append(field.PatternStdDevDb.ToString("0.0"))
              .Append(" dB   ").Append(field.StageLabel).Append('\n');

            sb.Append("IOP       ").Append(IopModel.Describe(c.IopMmHg, c.CentralCornealThicknessUm)).Append('\n');

            sb.Append('\n').Append("VIGNETTE\n").Append(Wrap(c.Vignette, 62)).Append('\n');
            sb.Append('\n').Append("TEACHING NOTE\n").Append(Wrap(c.TeachingNote, 62));

            return sb.ToString();
        }

        /// <summary>
        /// Hard-wraps text. The HUD uses a fixed-width layout and Unity's Text
        /// wrapping would reflow the panel height on every case change, making the
        /// readout jump around under the reader's eye.
        /// </summary>
        private static string Wrap(string text, int width)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            System.Text.StringBuilder sb = new System.Text.StringBuilder(text.Length + 32);
            int lineLength = 0;

            string[] words = text.Split(' ');
            for (int i = 0; i < words.Length; i++)
            {
                if (lineLength + words[i].Length > width && lineLength > 0)
                {
                    sb.Append('\n');
                    lineLength = 0;
                }

                sb.Append(words[i]).Append(' ');
                lineLength += words[i].Length + 1;
            }

            return sb.ToString();
        }

        // =====================================================================
        // UI construction
        // =====================================================================

        private void BuildUi()
        {
            // OrbitInspector asks EventSystem.current whether the pointer is over
            // UI before it orbits. With no EventSystem in the scene that call
            // returns null and the guard silently stops working.
            if (EventSystem.current == null)
            {
                GameObject es = new GameObject("EventSystem");
                es.AddComponent<EventSystem>();
                es.AddComponent<StandaloneInputModule>();
            }

            GameObject canvasGo = new GameObject("HUD Canvas");
            canvasGo.transform.SetParent(transform, false);

            Canvas canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1600f, 900f);
            scaler.matchWidthOrHeight = 1f;

            canvasGo.AddComponent<GraphicRaycaster>();

            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            _caseHeader = MakeLabel(canvasGo.transform, font, "Case", 22, TextAnchor.UpperLeft,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(24f, -20f),
                new Vector2(900f, 34f), Color.white, false);

            _metrics = MakeLabel(canvasGo.transform, font, "", 15, TextAnchor.UpperLeft,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(24f, -64f),
                new Vector2(560f, 720f), new Color(0.88f, 0.92f, 0.96f), true);

            _controls = MakeLabel(canvasGo.transform, font, ControlsText(), 15, TextAnchor.LowerRight,
                new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-24f, 20f),
                new Vector2(470f, 160f), new Color(0.72f, 0.78f, 0.85f), true);

            _planimetryText = MakeLabel(canvasGo.transform, font, "", 16, TextAnchor.UpperLeft,
                new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(24f, 130f),
                new Vector2(430f, 120f), new Color(0.92f, 0.88f, 0.70f), true);

            BuildXrControls(canvasGo.transform, font);
            BuildImportControls(canvasGo.transform, font);
        }

        /// <summary>
        /// "Load Eye Image" and the line underneath it that reports what the
        /// import is doing.
        ///
        /// <para>The status line is not decoration. Generation takes about a
        /// minute, and the measurements appear within a second of choosing the
        /// file -- so without a visible "generating" state the eye looks finished
        /// and then something moves a minute later for no apparent reason.</para>
        /// </summary>
        private void BuildImportControls(Transform parent, Font font)
        {
            _importButton = MakeButton(parent, font, "Load Eye Image...",
                new Vector2(-24f, -114f), new Vector2(352f, 42f), OnImportClicked);

            // The field angle sets the whole scale, so it belongs next to the
            // button that loads the image, not buried in an inspector the person
            // using a built player cannot reach.
            _fieldButton = MakeButton(parent, font, "Field: 30 deg",
                new Vector2(-24f, -162f), new Vector2(352f, 36f), OnFieldClicked);

            _importStatus = MakeLabel(parent, font, "", 14, TextAnchor.UpperRight,
                new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-24f, -204f),
                new Vector2(352f, 86f), new Color(0.78f, 0.86f, 0.95f), true);

            RefreshImport();
        }

        private void OnImportClicked()
        {
            if (_import != null) _import.PickAndImport();
        }

        private void OnFieldClicked()
        {
            if (_import != null) _import.CycleFieldAngle();
        }

        private void OnImportChanged(EyeImportController controller) { RefreshImport(); }

        private void RefreshImport()
        {
            if (_importButton == null) return;

            if (_import == null)
            {
                SetButton(_importButton, false, "Load Eye Image...");
                if (_importStatus != null) _importStatus.text = string.Empty;
                return;
            }

            bool ready = !_import.Busy && _import.CanPickFile;

            SetButton(_importButton, ready,
                _import.Busy ? "Working..." : "Load Eye Image...");

            if (_fieldButton != null)
                SetButton(_fieldButton, !_import.Busy,
                    "Field: " + _import.FieldAngleDeg.ToString("0") + " deg");

            if (_importStatus == null) return;

            string text = _import.Status;

            // A percentage only while something is actually running: a bar frozen
            // at 100% reads as stuck rather than finished.
            if (_import.Progress >= 0f)
                text += "  " + Mathf.RoundToInt(_import.Progress * 100f) + "%";

            _importStatus.text = Wrap(text, 44);        }

        /// <summary>
        /// VR and AR entry buttons.
        ///
        /// <para>These MUST be real clickable buttons. A browser will only start an
        /// XR session from a genuine user gesture, and it refuses SILENTLY -- a
        /// keyboard shortcut would look like a bug in our code rather than a policy
        /// in theirs.</para>
        /// </summary>
        private void BuildXrControls(Transform parent, Font font)
        {
            _xrStatus = MakeLabel(parent, font, "", 15, TextAnchor.UpperRight,
                new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-24f, -20f),
                new Vector2(360f, 34f), new Color(0.78f, 0.86f, 0.95f), true);

            _vrButton = MakeButton(parent, font, "Enter VR",
                new Vector2(-24f, -64f), new Vector2(170f, 42f), EnterVr);

            _arButton = MakeButton(parent, font, "Enter AR",
                new Vector2(-206f, -64f), new Vector2(170f, 42f), EnterAr);

            RefreshXr();
        }

        private void EnterVr() { if (_xr != null) _xr.EnterVr(); }
        private void EnterAr() { if (_xr != null) _xr.EnterAr(); }

        private void OnXrModeChanged(XrSessionController xr) { RefreshXr(); }

        private void RefreshXr()
        {
            if (_xrStatus == null) return;

            if (_xr == null)
            {
                _xrStatus.text = "Desktop";
                SetButton(_vrButton, false, "VR unavailable");
                SetButton(_arButton, false, "AR unavailable");
                return;
            }

            _xrStatus.text = _xr.ModeLabel;

            // Disabled buttons say WHY. "Enter VR" that does nothing when clicked is
            // indistinguishable from a broken build; "VR: headset not detected" tells
            // the user the software is fine and the hardware is the missing piece.
            if (!_xr.IsPackagePresent)
            {
                SetButton(_vrButton, false, "VR: package missing");
                SetButton(_arButton, false, "AR: package missing");
                return;
            }

            SetButton(_vrButton, _xr.IsVrSupported,
                _xr.IsVrSupported ? "Enter VR" : "VR: no headset");

            SetButton(_arButton, _xr.IsArSupported,
                _xr.IsArSupported ? "Enter AR" : "AR: not supported here");
        }

        private static void SetButton(Button button, bool interactable, string label)
        {
            if (button == null) return;

            button.interactable = interactable;

            Text text = button.GetComponentInChildren<Text>();
            if (text != null)
            {
                text.text = label;
                text.color = interactable ? Color.white : new Color(0.62f, 0.62f, 0.66f);
            }
        }

        private static Button MakeButton(Transform parent, Font font, string label,
                                         Vector2 position, Vector2 size, UnityEngine.Events.UnityAction onClick)
        {
            GameObject go = new GameObject("Button");
            go.transform.SetParent(parent, false);

            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.anchoredPosition = position;
            rt.sizeDelta = size;

            Image bg = go.AddComponent<Image>();
            bg.color = new Color(0.16f, 0.24f, 0.36f, 0.95f);
            bg.raycastTarget = true;   // unlike the panels, this one MUST take clicks

            Button button = go.AddComponent<Button>();
            button.targetGraphic = bg;
            button.onClick.AddListener(onClick);

            GameObject textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);

            RectTransform trt = textGo.AddComponent<RectTransform>();
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero;
            trt.offsetMax = Vector2.zero;

            Text text = textGo.AddComponent<Text>();
            text.font = font;
            text.fontSize = 16;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.text = label;
            text.raycastTarget = false;

            return button;
        }

        private static string ControlsText()
        {
            return "Drag  orbit     Wheel  zoom     Middle-drag  pan\n"
                 + "1  en-face     2  oblique     3  cross-section\n"
                 + "N / P  next / previous case      M  method      C  detail\n"
                 + "Load Eye Image   button, top right\n"
                 + "G  eye globe (ghost / cut / solid / off)\n"
                 + "B  begin planimetry     X  clear     U  use my trace\n"
                 + "Right-click  place point     Enter  close contour\n"
                 + "Backspace  undo point";
        }

        /// <summary>
        /// Planimetry panel. Deliberately shows the truth ONLY after both contours
        /// are closed - seeing the answer while still drawing turns an assessment
        /// into a tracing exercise.
        /// </summary>
        private void RefreshPlanimetry()
        {
            if (_planimetryText == null) return;

            if (_planimetry == null)
            {
                _planimetryText.text = "Planimetry unavailable";
                return;
            }

            System.Text.StringBuilder sb = new System.Text.StringBuilder(320);
            sb.Append("PLANIMETRY\n");

            switch (_planimetry.Stage)
            {
                case PlanimetryStage.Idle:
                    sb.Append("Press B to mark the disc and cup margins yourself,\n");
                    sb.Append("then compare against the measured truth.");
                    break;

                case PlanimetryStage.DrawingDisc:
                    sb.Append("Marking the DISC margin: ")
                      .Append(_planimetry.DiscPoints.Count).Append(" points\n");
                    sb.Append(_planimetry.DiscPoints.Count < PlanimetryTool.MinimumPoints
                        ? "Need at least " + PlanimetryTool.MinimumPoints + " points."
                        : "Enter to close the contour.");
                    break;

                case PlanimetryStage.DrawingCup:
                    sb.Append("Marking the CUP margin: ")
                      .Append(_planimetry.CupPoints.Count).Append(" points\n");
                    sb.Append(_planimetry.CupPoints.Count < PlanimetryTool.MinimumPoints
                        ? "Need at least " + PlanimetryTool.MinimumPoints + " points."
                        : "Enter to score.");
                    break;

                case PlanimetryStage.Complete:
                    PlanimetryResult r = _planimetry.LastResult;

                    sb.Append("Your vertical CDR   ").Append(r.UserVerticalCdr.ToString("0.00")).Append('\n');
                    sb.Append("Measured            ").Append(r.TruthVerticalCdr.ToString("0.00")).Append('\n');

                    // Signed, and named. "0.12 out" hides the lesson; "over-called"
                    // is the feedback that changes behaviour.
                    float e = r.VerticalErrorSigned;
                    string direction = Mathf.Abs(e) < 0.03f
                        ? "within 0.03 - good estimate"
                        : (e > 0f ? "you OVER-called the cup by " + e.ToString("0.00")
                                  : "you UNDER-called the cup by " + (-e).ToString("0.00"));

                    sb.Append(direction).Append('\n');
                    sb.Append("Your rim area       ").Append(r.UserRimAreaMm2.ToString("0.00")).Append(" mm2");
                    break;
            }

            _planimetryText.text = sb.ToString();
        }

        private static Text MakeLabel(Transform parent, Font font, string content, int size,
                                      TextAnchor anchor, Vector2 anchorMin, Vector2 anchorMax,
                                      Vector2 position, Vector2 size2, Color colour, bool panel)
        {
            GameObject go = new GameObject("Label");
            go.transform.SetParent(parent, false);

            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = new Vector2(anchorMin.x, anchorMax.y);
            rt.anchoredPosition = position;
            rt.sizeDelta = size2;

            if (panel)
            {
                Image bg = go.AddComponent<Image>();
                bg.color = PanelColour;
                // The panel must not swallow drags meant for the camera; only the
                // controls hint and readout live here and neither is interactive.
                bg.raycastTarget = false;
            }

            GameObject textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);

            RectTransform trt = textGo.AddComponent<RectTransform>();
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(12f, 10f);
            trt.offsetMax = new Vector2(-12f, -10f);

            Text text = textGo.AddComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.alignment = anchor;
            text.color = colour;
            text.text = content;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.supportRichText = false;

            return text;
        }
    }
}
