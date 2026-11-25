// -----------------------------------------------------------------------------
// Ocular -- what the frame of a fundus photograph tells you about its scale.
//
// Its own file because it decides which CALIBRATION is honest, which is the
// difference between a reported disc diameter being a measurement and being an
// artefact of how someone cropped a screenshot.
// -----------------------------------------------------------------------------

namespace Ocular.Core.Imaging
{
    public struct FundusFraming
    {
        /// <summary>Width of the illuminated region, pixels.</summary>
        public float FieldWidthPixels;

        /// <summary>Fraction of the frame that is unlit surround, 0..1.</summary>
        public float DarkFraction;

        /// <summary>
        /// True when the illuminated region runs off the edges, so the camera's
        /// stated field angle no longer describes what is in the frame.
        /// </summary>
        public bool LooksCropped;

        /// <summary>
        /// A line for the user explaining which calibration this framing forces
        /// and why, so the choice is never silent.
        /// </summary>
        public string Explain()
        {
            if (!LooksCropped)
            {
                return string.Format(
                    "Full fundus frame ({0:0}% surround): scaled from the camera field angle.",
                    DarkFraction * 100f);
            }

            return "This image is CROPPED -- the retina runs off the edges, so the camera's "
                 + "field angle does not describe it and cannot set the scale. Sizes are "
                 + "calibrated from an assumed 1.8 mm disc instead: ratios such as CDR and "
                 + "rim/disc remain valid, but disc and cup dimensions in millimetres are "
                 + "NOT measurements of this eye, and disc-size classification is suppressed. "
                 + "Supply an uncropped photograph, or state the true field angle, to measure "
                 + "real sizes.";
        }
    }
}
