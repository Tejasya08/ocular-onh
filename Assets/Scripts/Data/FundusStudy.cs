// -----------------------------------------------------------------------------
// Ocular -- one imported eye: a photograph, how it was scaled, and what was
// traced on it.
//
// ONE ScriptableObject type in this file, and the file is named after it. Unity
// binds a .asset to its script by file identity; a second SO type declared here
// would get a null script binding and load as an empty object with no error at
// all, and the study would silently open blank.
//
// A study is the INPUT side and stays editable: re-trace a margin, change the
// axial length, re-fit. The GlaucomaCase it produces is the output, and is
// generated rather than hand-edited, so that a case derived from an image can
// always be traced back to the image and the trace that produced it.
//
// Contours are stored in IMAGE PIXELS, not millimetres. Pixels are what the
// clinician actually placed and they do not move when the calibration changes --
// storing millimetres would silently rescale every previous trace the moment
// someone corrected an axial length.
//
// PRIVACY. A fundus photograph is patient data. Nothing here uploads or phones
// home, but a study asset committed to a repository carries the image with it.
// The shipped teaching cases use synthetic images for exactly that reason.
// -----------------------------------------------------------------------------

using UnityEngine;
using Ocular.Core.Anatomy;
using Ocular.Core.Imaging;

namespace Ocular.Data
{
    [CreateAssetMenu(fileName = "FundusStudy", menuName = "Ocular/Fundus Study")]
    public sealed class FundusStudy : ScriptableObject
    {
        [Header("Identity")]
        public string DisplayName = "Imported eye";

        [Tooltip("Which eye. Determines the x mirror used to convert pixels into disc-space.")]
        public Laterality Laterality = Laterality.OD;

        [Range(5, 95)]
        public int PatientAge = 60;

        [Header("Image")]
        [Tooltip("The colour fundus photograph. Must have Read/Write enabled to be projected.")]
        public Texture2D Photograph;

        [Tooltip("Optic disc centre, in image pixels, y measured DOWN from the top row.")]
        public Vector2 DiscCentrePixels;

        [Header("Calibration")]
        public ScaleSource ScaleSource = ScaleSource.CameraField;

        [Tooltip("Camera field angle in degrees. 30 and 45 are the common fundus camera settings.")]
        public float FieldAngleDeg = 30f;

        [Tooltip("Pixel width of the STATED FIELD -- the fundus circle, not the image file.")]
        public float FieldPixels = 1024f;

        [Tooltip("Axial length, mm. 23.5-24.5 is typical; longer in myopia, and it matters.")]
        public float AxialLengthMm = 24.0f;

        [Tooltip("Scale bar length in pixels. Used when ScaleSource is ScaleBar.")]
        public float ScaleBarPixels = 100f;

        [Tooltip("What that scale bar is worth in mm.")]
        public float ScaleBarMm = 1f;

        [Tooltip("Disc diameter to assume, mm. Used when ScaleSource is AssumedDiscDiameter, "
               + "which makes every reported disc size a restatement of this number.")]
        public float AssumedDiscDiameterMm = 1.8f;

        [Tooltip("Disc diameter in pixels for the assumed-diameter calibration.")]
        public float AssumedDiscDiameterPixels = 300f;

        [Header("Traced margins (image pixels)")]
        public Vector2[] DiscMargin;
        public Vector2[] CupMargin;

        [Tooltip("Optional outer edge of beta-zone peripapillary atrophy. Leave empty for none.")]
        public Vector2[] PpaMargin;

        [Header("Depth (not obtainable from a photograph)")]
        public DepthProvenance DepthProvenance = DepthProvenance.PopulationAssumed;

        [Tooltip("Cup depth in mm from OCT or a stereo estimate. Ignored when provenance is assumed.")]
        public float CupDepthMm = 0.3f;

        [Range(0f, 8f), Tooltip("Cup wall profile from OCT. 0 keeps the default.")]
        public float CupFloorFlatness;

        [Header("Clinical context")]
        public float IopMmHg = 15f;
        public float CentralCornealThicknessUm = 545f;
        public bool HasDiscHaemorrhage;
        public float HaemorrhageAngleDeg = 270f;

        [TextArea(3, 8)]
        public string Vignette = string.Empty;

        /// <summary>True when there is enough here to attempt a fit.</summary>
        public bool IsTraced
        {
            get
            {
                return DiscMargin != null && DiscMargin.Length >= 3
                    && CupMargin != null && CupMargin.Length >= 3;
            }
        }

        public ImageCalibration BuildCalibration()
        {
            switch (ScaleSource)
            {
                case ScaleSource.ScaleBar:
                    return ImageCalibration.FromScaleBar(ScaleBarPixels, ScaleBarMm);

                case ScaleSource.AssumedDiscDiameter:
                    return ImageCalibration.FromAssumedDiscDiameter(AssumedDiscDiameterPixels,
                                                                    AssumedDiscDiameterMm);

                default:
                    return ImageCalibration.FromCameraField(FieldAngleDeg, FieldPixels, AxialLengthMm);
            }
        }

        /// <summary>
        /// Assembles the fit inputs from the traces. Returns null when nothing has
        /// been traced yet, which is a normal state for a freshly created study
        /// rather than an error.
        /// </summary>
        public OnhFitInputs BuildFitInputs()
        {
            if (!IsTraced) return null;

            ImageCalibration calibration = BuildCalibration();

            OnhFitInputs inputs = new OnhFitInputs();

            inputs.Disc = PolarContour.FromPolygonPixels(DiscMargin, DiscCentrePixels,
                                                         calibration, Laterality).Smoothed(5);

            inputs.Cup = PolarContour.FromPolygonPixels(CupMargin, DiscCentrePixels,
                                                        calibration, Laterality).Smoothed(5);

            if (PpaMargin != null && PpaMargin.Length >= 3)
            {
                inputs.Ppa = PolarContour.FromPolygonPixels(PpaMargin, DiscCentrePixels,
                                                            calibration, Laterality).Smoothed(9);
            }

            inputs.CalibrationIsCircular = calibration.IsCircular;
            inputs.DepthProvenance = DepthProvenance;
            inputs.CupDepthMm = CupDepthMm;
            inputs.CupFloorFlatness = CupFloorFlatness;
            inputs.HasDiscHaemorrhage = HasDiscHaemorrhage;
            inputs.HaemorrhageAngleDeg = HaemorrhageAngleDeg;

            return inputs;
        }

        /// <summary>Fits this study. Null when it has not been traced.</summary>
        public OnhFitReport Fit()
        {
            OnhFitInputs inputs = BuildFitInputs();
            return inputs == null ? null : OnhSpecFitter.Fit(inputs);
        }

        /// <summary>
        /// Copies a fit and this study's clinical context onto a case.
        ///
        /// <para>Writes the caveats into the teaching note, so a case built from an
        /// assumed calibration carries that warning wherever it goes. A case that
        /// has been separated from its study is otherwise indistinguishable from a
        /// hand-authored one.</para>
        /// </summary>
        public void ApplyTo(GlaucomaCase target, OnhFitReport report)
        {
            if (target == null || report == null) return;

            target.DisplayName = DisplayName;
            target.Laterality = Laterality;
            target.PatientAge = PatientAge;
            target.Onh = report.Spec;
            target.IopMmHg = IopMmHg;
            target.CentralCornealThicknessUm = CentralCornealThicknessUm;
            target.Vignette = Vignette;

            string note = "Fitted from a fundus photograph. " + report.Summary();

            for (int i = 0; i < report.Caveats.Count; i++) note += "\n\n" + report.Caveats[i];

            target.TeachingNote = note;
        }
    }
}
