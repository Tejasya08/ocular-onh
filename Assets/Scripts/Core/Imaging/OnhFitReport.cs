// -----------------------------------------------------------------------------
// Ocular -- what came out of fitting a photograph, and how much of it you are
// entitled to believe.
//
// The report exists because a fitted spec on its own is indistinguishable from
// an authored one. Once OnhSpecFitter hands back an OpticNerveHeadSpec it looks
// exactly like a preset: same struct, same fields, and the surface built from it
// renders and measures identically. Nothing in the geometry remembers that the
// cup depth was a population average rather than an OCT reading, or that the
// disc diameter was assumed into existence by the calibration.
//
// So the provenance travels alongside, and the UI is expected to show it. A
// number whose provenance is Assumed is not a measurement of this patient; it is
// a placeholder that happens to be the right shape.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using Ocular.Core.Anatomy;

namespace Ocular.Core.Imaging
{
    /// <summary>
    /// Where the cup DEPTH came from.
    ///
    /// <para>Its own type because depth is the one quantity a colour fundus
    /// photograph fundamentally cannot contain. A single photograph is a
    /// projection; the cup's depth is exactly the dimension that projection threw
    /// away. Every other fitted parameter is a genuine measurement off the image
    /// and this one never is, so it cannot share their status.</para>
    /// </summary>
    public enum DepthProvenance
    {
        /// <summary>From an OCT scan of this eye. The only case that is a measurement.</summary>
        OctMeasured,

        /// <summary>From a stereo pair or a photogrammetric estimate. Real signal, wide error bars.</summary>
        StereoEstimated,

        /// <summary>A population figure scaled by the fitted CDR. A plausible shape, not this patient's.</summary>
        PopulationAssumed
    }

    public sealed class OnhFitReport
    {
        /// <summary>The fitted ground truth. Feeding this to OnhSurface reproduces the trace.</summary>
        public OpticNerveHeadSpec Spec;

        /// <summary>
        /// False when the calibration assumed a disc size. Absolute disc and cup
        /// dimensions must then be labelled, and disc-size classification suppressed.
        /// Ratios such as CDR remain valid either way.
        /// </summary>
        public bool DiscDiameterIsMeasured = true;

        public DepthProvenance DepthProvenance = DepthProvenance.PopulationAssumed;

        /// <summary>RMS difference between the traced disc margin and the rebuilt surface's, mm.</summary>
        public float DiscResidualRmsMm;

        /// <summary>RMS difference between the traced cup margin and the rebuilt surface's, mm.</summary>
        public float CupResidualRmsMm;

        /// <summary>Worst single-meridian disagreement across both contours, mm.</summary>
        public float MaxResidualMm;

        /// <summary>True when a focal notch survived the detection threshold.</summary>
        public bool NotchDetected;

        /// <summary>Free-text warnings, each safe to show verbatim in the UI.</summary>
        public readonly List<string> Caveats = new List<string>();

        /// <summary>
        /// A fit is usable when the rebuilt surface tracks the trace to well inside
        /// the width of a drawn line. 0.05 mm is roughly 3% of a normal disc
        /// diameter, and comfortably below the interobserver spread on where the
        /// disc margin actually is.
        /// </summary>
        public const float AcceptableResidualMm = 0.05f;

        public bool IsGoodFit
        {
            get { return DiscResidualRmsMm <= AcceptableResidualMm && CupResidualRmsMm <= AcceptableResidualMm; }
        }

        public void AddCaveat(string caveat)
        {
            if (!string.IsNullOrEmpty(caveat) && !Caveats.Contains(caveat)) Caveats.Add(caveat);
        }

        /// <summary>How a cup depth of this provenance should be labelled on screen.</summary>
        public string DepthLabel()
        {
            switch (DepthProvenance)
            {
                case DepthProvenance.OctMeasured: return "OCT";
                case DepthProvenance.StereoEstimated: return "stereo est.";
                default: return "ASSUMED";
            }
        }

        public string Summary()
        {
            return string.Format(
                "disc RMS {0:0.000} mm, cup RMS {1:0.000} mm, worst {2:0.000} mm; "
                + "disc size {3}; depth {4}; notch {5}",
                DiscResidualRmsMm, CupResidualRmsMm, MaxResidualMm,
                DiscDiameterIsMeasured ? "measured" : "ASSUMED",
                DepthLabel(),
                NotchDetected ? "detected" : "none");
        }
    }
}
