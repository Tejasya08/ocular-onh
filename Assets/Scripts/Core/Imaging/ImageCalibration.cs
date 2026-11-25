// -----------------------------------------------------------------------------
// Ocular -- pixels to millimetres, and the one way of doing it that quietly
// invalidates half the output.
//
// THE CIRCULARITY TRAP
//
// The easiest way to scale a fundus photograph is to say "the optic disc is
// about 1.8 mm across" and divide. It is also the one calibration that makes the
// disc diameter unmeasurable: you have assumed the answer, so the pipeline will
// dutifully report 1.8 mm no matter what disc is in the photograph, and it will
// report it in the same font, with the same decimal places, as a real
// measurement. Disc size is the single most important context for reading a CDR
// -- a large disc carries a large cup on a normal amount of rim -- so this is
// not a small caveat, it is a hole straight through the clinical reasoning.
//
// It is still supported, because sometimes it is all you have. But it sets
// IsCircular, the fitter propagates that into OnhFitReport.DiscDiameterIsMeasured,
// and anything displaying a disc diameter is expected to label it as ASSUMED.
// OnhSpecFitterTests asserts that the flag survives the whole way through.
//
// UNITS: millimetres of RETINAL surface, not millimetres of image.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace Ocular.Core.Imaging
{
    /// <summary>Where a calibration's scale factor came from. Governs what may be reported.</summary>
    public enum ScaleSource
    {
        /// <summary>A physical scale bar or a known graticule burned into the image. Best case.</summary>
        ScaleBar,

        /// <summary>Camera field angle plus the eye's axial length. The normal case.</summary>
        CameraField,

        /// <summary>An assumed disc diameter. Convenient, and makes disc size unreportable.</summary>
        AssumedDiscDiameter
    }

    public sealed class ImageCalibration
    {
        /// <summary>
        /// Retinal arc length per degree of visual angle in a standard eye, mm.
        /// The conventional teaching figure; exact value depends on the schematic
        /// eye chosen and varies in the literature between about 0.28 and 0.30.
        /// </summary>
        public const float MmPerDegreeStandard = 0.29f;

        /// <summary>Axial length of the schematic eye the above figure belongs to, mm.</summary>
        public const float StandardAxialLengthMm = 24.46f;

        /// <summary>
        /// Distance from the corneal vertex to the second principal plane, mm.
        /// The subtraction in Bennett's correction: only the length BEHIND the
        /// principal plane scales the retinal image.
        /// </summary>
        public const float BennettAnteriorOffsetMm = 1.82f;

        public float PixelsPerMm { get; private set; }
        public ScaleSource Source { get; private set; }

        /// <summary>Axial length this calibration assumed, mm. 0 when not applicable.</summary>
        public float AxialLengthMm { get; private set; }

        /// <summary>
        /// True when the scale was derived from an assumed disc size, which makes
        /// any reported disc dimension a restatement of that assumption.
        /// </summary>
        public bool IsCircular { get { return Source == ScaleSource.AssumedDiscDiameter; } }

        private ImageCalibration(float pixelsPerMm, ScaleSource source, float axialLengthMm)
        {
            // A non-positive scale divides by zero in every consumer and produces
            // NaN contours, which fit to a NaN spec and render as an invisible
            // mesh with no error anywhere. Clamp rather than throw.
            PixelsPerMm = Mathf.Clamp(pixelsPerMm, 1e-3f, 1e5f);
            Source = source;
            AxialLengthMm = axialLengthMm;
        }

        /// <summary>A known distance in the image. The only calibration with no assumptions in it.</summary>
        public static ImageCalibration FromScaleBar(float lengthPixels, float lengthMm)
        {
            float mm = Mathf.Max(lengthMm, 1e-4f);
            return new ImageCalibration(lengthPixels / mm, ScaleSource.ScaleBar, 0f);
        }

        /// <summary>
        /// Camera field angle plus axial length -- the correct general method.
        ///
        /// <para><paramref name="fieldPixels"/> is the pixel width of the stated
        /// field, which is the width of the circular fundus image, NOT the width
        /// of the file. A photograph letterboxed into a 1920-wide frame with the
        /// fundus circle only 1400 px across is scaled 37% wrong if you pass the
        /// file width.</para>
        ///
        /// <para>The axial length term matters more than it looks. A myopic eye is
        /// longer, so its retinal image is larger, so its disc occupies more pixels
        /// at the same field angle. Skip the correction and every myopic disc is
        /// reported smaller than it is -- and myopic discs, tilted and large, are
        /// exactly the ones that get over-called as glaucomatous. The error runs in
        /// the direction that reinforces the mistake.</para>
        /// </summary>
        public static ImageCalibration FromCameraField(float fieldAngleDeg, float fieldPixels, float axialLengthMm)
        {
            fieldAngleDeg = Mathf.Clamp(fieldAngleDeg, 5f, 200f);
            axialLengthMm = Mathf.Clamp(axialLengthMm, 18f, 34f);

            float bennett = (axialLengthMm - BennettAnteriorOffsetMm)
                          / (StandardAxialLengthMm - BennettAnteriorOffsetMm);

            float fieldMm = fieldAngleDeg * MmPerDegreeStandard * bennett;

            return new ImageCalibration(fieldPixels / Mathf.Max(fieldMm, 1e-4f),
                                        ScaleSource.CameraField, axialLengthMm);
        }

        /// <summary>
        /// Scale from an assumed disc diameter. Sets <see cref="IsCircular"/>.
        ///
        /// <para>Everything downstream of this calibration is still valid EXCEPT
        /// absolute disc and cup dimensions. Ratios survive -- CDR is a ratio of
        /// two lengths in the same image, so the scale cancels -- which is why this
        /// is worth supporting at all rather than refusing.</para>
        /// </summary>
        public static ImageCalibration FromAssumedDiscDiameter(float discDiameterPixels, float assumedDiameterMm)
        {
            float mm = Mathf.Clamp(assumedDiameterMm, 0.8f, 3.2f);
            return new ImageCalibration(discDiameterPixels / mm, ScaleSource.AssumedDiscDiameter, 0f);
        }

        public float PixelsToMm(float pixels) { return pixels / PixelsPerMm; }

        public float MmToPixels(float mm) { return mm * PixelsPerMm; }

        public string Describe()
        {
            switch (Source)
            {
                case ScaleSource.ScaleBar:
                    return string.Format("{0:0.0} px/mm from scale bar", PixelsPerMm);

                case ScaleSource.CameraField:
                    return string.Format("{0:0.0} px/mm from camera field, axial length {1:0.0} mm",
                                         PixelsPerMm, AxialLengthMm);

                default:
                    return string.Format("{0:0.0} px/mm from an ASSUMED disc diameter "
                                       + "-- ratios valid, absolute sizes are not measurements",
                                         PixelsPerMm);
            }
        }
    }
}
