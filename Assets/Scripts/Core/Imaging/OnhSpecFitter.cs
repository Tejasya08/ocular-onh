// -----------------------------------------------------------------------------
// Ocular -- turning two traced outlines into an OpticNerveHeadSpec.
//
// This is what makes the simulation image-derived rather than invented: a real
// disc margin and a real cup margin, taken off a real photograph of a real eye,
// become the parametric ground truth that the mesh is generated from.
//
// WHAT THIS DOES NOT DO, and it matters: it does not shortcut the round trip.
// The fitter produces a spec; OnhSurface generates a surface from that spec; the
// measurement engine then measures the surface. The CDR that appears on screen
// is still recovered from geometry -- it is not the ratio this file computed and
// stashed. Because of that, a fitting error shows up as a residual (the rebuilt
// surface stops matching the trace) instead of silently propagating into a
// confident-looking number, and OnhSpecFitterTests can assert the whole loop.
//
// The fit is deliberately cup-exact. The model's disc margin is an ellipse and
// cannot be anything else, so the traced disc's departure from an ellipse has to
// land somewhere. Putting it in the disc residual and keeping the cup margin
// exact is the right way round: the cup margin is what CDR, rim width, ISNT and
// DDLS are all computed from, whereas disc ellipticity is a separate, honestly
// reportable property of that disc.
//
// UNITS: millimetres and radians throughout. No pixels reach this file.
// -----------------------------------------------------------------------------

using UnityEngine;
using Ocular.Core.Anatomy;

namespace Ocular.Core.Imaging
{
    /// <summary>Everything the fitter needs. Contours are required; the rest has defaults.</summary>
    public sealed class OnhFitInputs
    {
        public PolarContour Disc;
        public PolarContour Cup;

        /// <summary>Optional outer edge of beta-zone atrophy. Null when none was traced.</summary>
        public PolarContour Ppa;

        /// <summary>Set from ImageCalibration.IsCircular. Propagates into the report.</summary>
        public bool CalibrationIsCircular;

        /// <summary>Cup depth in mm when a real one is available. Ignored unless provenance says so.</summary>
        public float CupDepthMm;

        public DepthProvenance DepthProvenance = DepthProvenance.PopulationAssumed;

        /// <summary>Cup wall profile. 0 leaves the healthy default.</summary>
        public float CupFloorFlatness;

        public bool HasDiscHaemorrhage;
        public float HaemorrhageAngleDeg = 270f;
    }

    public static class OnhSpecFitter
    {
        /// <summary>Angular samples used for the rim fit and the residuals.</summary>
        public const int FitSamples = 360;

        /// <summary>
        /// Rim loss below this fraction is not called a notch.
        ///
        /// <para>Set from what survives smoothing rather than from clinical
        /// significance. A traced margin still carries a few percent of angular
        /// noise, and the detector takes the single deepest dip around the whole
        /// circle -- the maximum of ~360 noisy samples is biased upward, so a
        /// threshold near zero finds a notch in every normal disc it is shown.</para>
        /// </summary>
        public const float NotchMinDepth = 0.12f;

        /// <summary>
        /// Population cup depth for a given vertical CDR, mm. A TEACHING HEURISTIC.
        ///
        /// <para>Used only when no OCT depth is supplied, and always reported as
        /// DepthProvenance.PopulationAssumed. Deeper cups do go with larger ones,
        /// but the scatter around this line is enormous -- an excavated
        /// normal-tension disc and a shallow large physiologic cup can share a CDR
        /// and differ in depth by a factor of three. That is precisely the
        /// distinction a photograph cannot make and OCT can.</para>
        /// </summary>
        public static float PopulationDepthForCdr(float cdrVertical)
        {
            return Mathf.Clamp(0.14f + 0.55f * Mathf.Clamp01(cdrVertical), 0.05f, 1.0f);
        }

        public static OnhFitReport Fit(OnhFitInputs inputs)
        {
            OnhFitReport report = new OnhFitReport();

            if (inputs == null || inputs.Disc == null || inputs.Cup == null)
            {
                report.Spec = OpticNerveHeadSpec.Healthy();
                report.AddCaveat("No contours supplied; returned a healthy default disc.");
                return report;
            }

            OpticNerveHeadSpec spec = OpticNerveHeadSpec.Healthy();

            // --- Disc and cup dimensions ----------------------------------------
            float discV = Mathf.Max(inputs.Disc.VerticalDiameterMm, 1e-3f);
            float discH = Mathf.Max(inputs.Disc.HorizontalDiameterMm, 1e-3f);

            float cupV = Mathf.Max(inputs.Cup.VerticalDiameterMm, 0f);
            float cupH = Mathf.Max(inputs.Cup.HorizontalDiameterMm, 0f);

            spec.DiscDiameterVerticalMm = discV;
            spec.DiscDiameterHorizontalMm = discH;
            spec.CupToDiscVertical = Mathf.Clamp(cupV / discV, 0f, 0.95f);
            spec.CupToDiscHorizontal = Mathf.Clamp(cupH / discH, 0f, 0.95f);

            // --- Depth, which no photograph contains -----------------------------
            if (inputs.DepthProvenance == DepthProvenance.PopulationAssumed)
            {
                spec.CupDepthMm = PopulationDepthForCdr(spec.CupToDiscVertical);
                report.AddCaveat("Cup depth is a population estimate, not a measurement of this eye. "
                               + "A colour fundus photograph carries no depth information; "
                               + "supply an OCT depth to make it real.");
            }
            else
            {
                spec.CupDepthMm = inputs.CupDepthMm;
            }

            report.DepthProvenance = inputs.DepthProvenance;

            if (inputs.CupFloorFlatness > 0f) spec.CupFloorFlatness = inputs.CupFloorFlatness;

            // --- Per-sector rim and focal notch -----------------------------------
            float discSemiX = discH * 0.5f;
            float discSemiY = discV * 0.5f;
            float cupSemiX = discSemiX * spec.CupToDiscHorizontal;
            float cupSemiY = discSemiY * spec.CupToDiscVertical;

            float[] theta = new float[FitSamples];
            float[] f = new float[FitSamples];

            for (int i = 0; i < FitSamples; i++)
            {
                float t = (i + 0.5f) / FitSamples * DiscGeometry.TwoPi;
                theta[i] = t;

                float ellipseDisc = DiscGeometry.EllipseRadius(discSemiX, discSemiY, t);
                float ellipseCup = DiscGeometry.EllipseRadius(cupSemiX, cupSemiY, t);
                float baseRim = Mathf.Max(ellipseDisc - ellipseCup, 1e-5f);

                // Cup-exact: the rim the model must produce is the gap between the
                // model's own elliptical disc margin and the TRACED cup margin.
                float requiredRim = Mathf.Clamp(ellipseDisc - inputs.Cup.RadiusAt(t), 0f, ellipseDisc);

                f[i] = requiredRim / baseRim;
            }

            float[] weights = new float[FitSamples];
            for (int i = 0; i < FitSamples; i++) weights[i] = 1f;

            float[] k = SolveSectorScales(theta, f, weights);

            // Two passes. A deep notch drags the sector scale of the sector it sits
            // in down with it, so the first pass under-reports the surrounding rim
            // and correspondingly under-reports the notch depth. Re-fitting with the
            // notch region down-weighted separates the two. A third pass moves
            // nothing measurable.
            float notchCentre, notchDepth, notchSigma;
            DetectNotch(theta, f, k, out notchCentre, out notchDepth, out notchSigma);

            if (notchDepth >= NotchMinDepth)
            {
                for (int i = 0; i < FitSamples; i++)
                {
                    bool insideNotch = DiscGeometry.AngleDelta(theta[i], notchCentre) < 2f * notchSigma;
                    weights[i] = insideNotch ? 0.1f : 1f;
                }

                k = SolveSectorScales(theta, f, weights);
                DetectNotch(theta, f, k, out notchCentre, out notchDepth, out notchSigma);
            }

            spec.RimScaleInferior = Mathf.Clamp(k[(int)DiscSector.Inferior], 0.05f, 2f);
            spec.RimScaleSuperior = Mathf.Clamp(k[(int)DiscSector.Superior], 0.05f, 2f);
            spec.RimScaleNasal = Mathf.Clamp(k[(int)DiscSector.Nasal], 0.05f, 2f);
            spec.RimScaleTemporal = Mathf.Clamp(k[(int)DiscSector.Temporal], 0.05f, 2f);

            if (notchDepth >= NotchMinDepth)
            {
                spec.NotchAngleDeg = Mathf.Repeat(notchCentre * Mathf.Rad2Deg, 360f);
                spec.NotchDepth = Mathf.Clamp01(notchDepth);
                spec.NotchWidthDeg = Mathf.Clamp(notchSigma * Mathf.Rad2Deg, 2f, 60f);
                report.NotchDetected = true;
            }
            else
            {
                spec.NotchDepth = 0f;
            }

            // --- Peripapillary atrophy --------------------------------------------
            if (inputs.Ppa != null) FitPpa(inputs, ref spec, report);

            spec.HasDiscHaemorrhage = inputs.HasDiscHaemorrhage;
            spec.HaemorrhageAngleDeg = inputs.HaemorrhageAngleDeg;

            report.Spec = spec.Clamped();

            // --- Provenance and goodness of fit -----------------------------------
            report.DiscDiameterIsMeasured = !inputs.CalibrationIsCircular;

            if (inputs.CalibrationIsCircular)
            {
                report.AddCaveat("Scale came from an ASSUMED disc diameter, so disc and cup sizes in "
                               + "millimetres restate that assumption. Ratios (CDR, rim/disc) are "
                               + "unaffected; disc-size classification is not valid.");
            }

            ComputeResiduals(report, inputs);

            if (!report.IsGoodFit)
            {
                report.AddCaveat(string.Format(
                    "The rebuilt surface does not follow the trace to within {0:0.00} mm. "
                    + "Usually a mis-placed centre or a contour that doubles back; re-check the trace.",
                    OnhFitReport.AcceptableResidualMm));
            }

            return report;
        }

        /// <summary>
        /// Weighted least squares for the four sector rim scales.
        ///
        /// <para>DiscGeometry.SectorWeight is a cos-squared partition of unity, so
        /// at a sector centre exactly one lobe is non-zero and equals 1. That is
        /// what keeps the fit self-consistent with the CDR read off the cardinal
        /// diameters: at theta = pi/2 the fit reproduces f exactly, so the rebuilt
        /// superior cup radius lands on the traced one, and the vertical CDR
        /// recovered by the measurement engine matches the one measured off the
        /// image. Nothing is double-counted.</para>
        /// </summary>
        private static float[] SolveSectorScales(float[] theta, float[] f, float[] weights)
        {
            float[,] a = new float[4, 4];
            float[] b = new float[4];

            float[] centre = new float[4];
            for (int s = 0; s < 4; s++) centre[s] = DiscGeometry.SectorCentre((DiscSector)s);

            float[] w = new float[4];

            for (int i = 0; i < theta.Length; i++)
            {
                for (int s = 0; s < 4; s++) w[s] = DiscGeometry.SectorWeight(theta[i], centre[s]);

                float m = weights[i];

                for (int s = 0; s < 4; s++)
                {
                    b[s] += m * w[s] * f[i];
                    for (int t = 0; t < 4; t++) a[s, t] += m * w[s] * w[t];
                }
            }

            float[] k;
            if (!Solve4(a, b, out k))
            {
                // Singular only if some sector had no angular support at all, which
                // cannot happen with a full 360-degree contour. Leaving the rim
                // unscaled is a visibly wrong disc rather than a NaN mesh.
                k = new float[] { 1f, 1f, 1f, 1f };
            }

            return k;
        }

        /// <summary>Gaussian elimination with partial pivoting on a 4x4.</summary>
        private static bool Solve4(float[,] a, float[] b, out float[] x)
        {
            x = new float[4];

            float[,] m = new float[4, 5];
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++) m[r, c] = a[r, c];
                m[r, 4] = b[r];
            }

            for (int col = 0; col < 4; col++)
            {
                int pivot = col;
                for (int r = col + 1; r < 4; r++)
                    if (Mathf.Abs(m[r, col]) > Mathf.Abs(m[pivot, col])) pivot = r;

                if (Mathf.Abs(m[pivot, col]) < 1e-9f) return false;

                if (pivot != col)
                    for (int c = 0; c <= 4; c++)
                    {
                        float tmp = m[col, c];
                        m[col, c] = m[pivot, c];
                        m[pivot, c] = tmp;
                    }

                for (int r = 0; r < 4; r++)
                {
                    if (r == col) continue;

                    float factor = m[r, col] / m[col, col];
                    for (int c = col; c <= 4; c++) m[r, c] -= factor * m[col, c];
                }
            }

            for (int r = 0; r < 4; r++) x[r] = m[r, 4] / m[r, r];

            return true;
        }

        /// <summary>
        /// Finds the deepest focal departure from the smooth four-sector rim.
        ///
        /// <para>Sigma is read off where the residual has climbed back to
        /// 1 - 0.6065 * depth, which is the Gaussian's value at one sigma. Fitting
        /// the width this way rather than by another least squares keeps the notch
        /// from competing with the sector scales for the same rim loss.</para>
        /// </summary>
        private static void DetectNotch(float[] theta, float[] f, float[] k,
                                        out float centreRad, out float depth, out float sigmaRad)
        {
            int n = theta.Length;
            float[] g = new float[n];

            float[] centre = new float[4];
            for (int s = 0; s < 4; s++) centre[s] = DiscGeometry.SectorCentre((DiscSector)s);

            for (int i = 0; i < n; i++)
            {
                float smooth = 0f;
                for (int s = 0; s < 4; s++) smooth += DiscGeometry.SectorWeight(theta[i], centre[s]) * k[s];

                g[i] = f[i] / Mathf.Max(smooth, 1e-4f);
            }

            int minIndex = 0;
            for (int i = 1; i < n; i++) if (g[i] < g[minIndex]) minIndex = i;

            centreRad = theta[minIndex];
            depth = Mathf.Clamp01(1f - g[minIndex]);

            float step = DiscGeometry.TwoPi / n;

            if (depth < 1e-4f)
            {
                sigmaRad = 10f * Mathf.Deg2Rad;
                return;
            }

            float level = 1f - 0.6065f * depth;

            int forward = 0;
            while (forward < n / 2 && g[(minIndex + forward) % n] < level) forward++;

            int backward = 0;
            while (backward < n / 2 && g[((minIndex - backward) % n + n) % n] < level) backward++;

            float halfWidth = 0.5f * (forward + backward) * step;

            sigmaRad = Mathf.Clamp(halfWidth, 2f * Mathf.Deg2Rad, 60f * Mathf.Deg2Rad);
        }

        /// <summary>
        /// Beta-zone atrophy from an outer crescent trace: width where the crescent
        /// stands outside the disc margin, and the arc over which it does so.
        /// </summary>
        private static void FitPpa(OnhFitInputs inputs, ref OpticNerveHeadSpec spec, OnhFitReport report)
        {
            const float MinWidthMm = 0.02f;

            float sumWidth = 0f;
            float sumX = 0f;
            float sumY = 0f;
            int count = 0;

            for (int i = 0; i < FitSamples; i++)
            {
                float t = (i + 0.5f) / FitSamples * DiscGeometry.TwoPi;
                float width = inputs.Ppa.RadiusAt(t) - inputs.Disc.RadiusAt(t);

                if (width <= MinWidthMm) continue;

                sumWidth += width;
                sumX += Mathf.Cos(t);
                sumY += Mathf.Sin(t);
                count++;
            }

            if (count == 0)
            {
                spec.PpaWidthMm = 0f;
                return;
            }

            spec.PpaWidthMm = sumWidth / count;

            // Circular mean, not an arithmetic one: a crescent straddling theta = 0
            // averages angles near 350 and near 10 to 180 if you take the plain mean,
            // and puts a temporal crescent on the nasal side.
            spec.PpaAngleDeg = Mathf.Repeat(Mathf.Atan2(sumY, sumX) * Mathf.Rad2Deg, 360f);
            spec.PpaArcDeg = Mathf.Clamp((float)count / FitSamples * 360f, 10f, 360f);

            report.AddCaveat("Beta-zone atrophy was fitted as a single crescent. A disc with atrophy "
                           + "in two separate arcs is modelled as one arc spanning both.");
        }

        /// <summary>
        /// Rebuilds the surface from the fitted spec and measures how far it strays
        /// from the traces. This is the check that the round trip actually closed.
        /// </summary>
        private static void ComputeResiduals(OnhFitReport report, OnhFitInputs inputs)
        {
            OnhSurface surface = new OnhSurface(report.Spec);

            double discSq = 0.0;
            double cupSq = 0.0;
            float worst = 0f;

            for (int i = 0; i < FitSamples; i++)
            {
                float t = (i + 0.5f) / FitSamples * DiscGeometry.TwoPi;

                float dDisc = surface.DiscRadiusMm(t) - inputs.Disc.RadiusAt(t);
                float dCup = surface.CupRadiusMm(t) - inputs.Cup.RadiusAt(t);

                discSq += dDisc * dDisc;
                cupSq += dCup * dCup;

                worst = Mathf.Max(worst, Mathf.Max(Mathf.Abs(dDisc), Mathf.Abs(dCup)));
            }

            report.DiscResidualRmsMm = Mathf.Sqrt((float)(discSq / FitSamples));
            report.CupResidualRmsMm = Mathf.Sqrt((float)(cupSq / FitSamples));
            report.MaxResidualMm = worst;
        }
    }
}
