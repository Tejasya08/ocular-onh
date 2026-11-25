// -----------------------------------------------------------------------------
// Ocular -- a 24-2 style visual field derived from nerve head damage.
//
// THE INVERSION. This is the single easiest thing in the whole project to get
// backwards, and getting it backwards produces output that looks entirely
// plausible and teaches the exact opposite of the truth. So the derivation is
// written out rather than asserted:
//
//   1. The eye's optics invert. Light from the SUPERIOR visual field lands on
//      the INFERIOR retina.
//   2. Nerve fibres from the inferior retina enter the disc INFERIORLY.
//   3. Therefore damage to the INFERIOR neuroretinal rim destroys the bundle
//      serving the SUPERIOR visual field.
//
//   => An inferior notch produces a SUPERIOR arcuate scotoma.
//
// In code that is the single negation in FieldPointToDiscAngle: retinal
// position is the field position negated, and the disc angle is the direction
// of that retinal position. VisualFieldTests asserts the result end to end, so
// if someone ever "fixes" the sign the suite fails rather than the teaching.
//
// Field coordinates are TEMPORAL-POSITIVE: +x is the temporal field, +y is the
// superior field, for whichever eye is being modelled. Laterality is carried
// only so the renderer can mirror the plot; no maths here depends on it.
//
// This is a teaching approximation of perimetry, not a simulation of one. It has
// no short-term fluctuation, no false positives, no fatigue effect and no
// age correction -- all of which dominate real field interpretation.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using Ocular.Core.Anatomy;

namespace Ocular.Core.Measurement
{
    /// <summary>Hodapp-Parrish-Anderson severity, staged on mean deviation.</summary>
    public enum FieldStage
    {
        WithinNormalLimits,
        Mild,
        Moderate,
        Severe
    }

    public struct VisualFieldPoint
    {
        public float XDeg;          // + temporal
        public float YDeg;          // + superior
        public float NormalDb;      // age-ignorant expected sensitivity
        public float MeasuredDb;
        public float DeviationDb;   // measured - normal, so loss is NEGATIVE
        public bool IsBlindSpot;
    }

    public sealed class VisualFieldResult
    {
        public VisualFieldPoint[] Points;
        public float MeanDeviationDb;
        public float PatternStdDevDb;
        public FieldStage Stage;
        public string StageLabel;
        public Laterality Laterality;
    }

    public static class VisualFieldModel
    {
        /// <summary>Sensitivity at fixation, dB, before eccentricity falloff.</summary>
        public const float FoveaNormalDb = 30.5f;

        /// <summary>Normal sensitivity falls roughly this much per degree of eccentricity.</summary>
        public const float FalloffDbPerDeg = 0.18f;

        /// <summary>Deepest loss a totally destroyed bundle can produce, dB.</summary>
        public const float MaxLossDb = 28f;

        /// <summary>Blind spot centre in the TEMPORAL field, degrees.</summary>
        public const float BlindSpotXDeg = 15f;
        public const float BlindSpotYDeg = -1.5f;
        public const float BlindSpotRadiusDeg = 4f;

        /// <summary>
        /// Radius within which the papillomacular bundle dominates, degrees.
        /// These fibres enter the disc temporally and are famously spared until
        /// late, which is why a patient with advanced field loss can still read
        /// the bottom line of the chart and believe nothing is wrong.
        /// </summary>
        public const float PapillomacularRadiusDeg = 10f;

        /// <summary>
        /// Builds the 24-2 style test grid: 6 degree spacing out to 21 degrees,
        /// corners dropped, plus the two nasal points that make a nasal step
        /// visible. Close to the clinical 24-2 layout, not identical to it.
        /// </summary>
        public static VisualFieldPoint[] BuildGrid()
        {
            List<VisualFieldPoint> points = new List<VisualFieldPoint>(64);
            float[] coords = { -21f, -15f, -9f, -3f, 3f, 9f, 15f, 21f };

            for (int yi = 0; yi < coords.Length; yi++)
            {
                for (int xi = 0; xi < coords.Length; xi++)
                {
                    float x = coords[xi];
                    float y = coords[yi];

                    // Drop the four extreme corners, as the clinical pattern does.
                    if (Mathf.Abs(x) > 20f && Mathf.Abs(y) > 20f) continue;

                    points.Add(MakePoint(x, y));
                }
            }

            // Nasal extension. The nasal step straddles the horizontal meridian, so
            // it needs a point either side of it out at the nasal edge.
            points.Add(MakePoint(-27f, 3f));
            points.Add(MakePoint(-27f, -3f));

            return points.ToArray();
        }

        private static VisualFieldPoint MakePoint(float x, float y)
        {
            VisualFieldPoint p = new VisualFieldPoint();
            p.XDeg = x;
            p.YDeg = y;
            p.NormalDb = NormalSensitivityDb(x, y);
            p.IsBlindSpot = IsInBlindSpot(x, y);
            return p;
        }

        public static bool IsInBlindSpot(float xDeg, float yDeg)
        {
            float dx = xDeg - BlindSpotXDeg;
            float dy = yDeg - BlindSpotYDeg;
            return Mathf.Sqrt(dx * dx + dy * dy) <= BlindSpotRadiusDeg;
        }

        public static float NormalSensitivityDb(float xDeg, float yDeg)
        {
            float ecc = Mathf.Sqrt(xDeg * xDeg + yDeg * yDeg);
            return FoveaNormalDb - FalloffDbPerDeg * ecc;
        }

        /// <summary>
        /// Disc-space angle of the nerve fibre bundle serving a visual field point.
        ///
        /// <para>The negation IS the optical inversion -- see the header. A superior
        /// field point (y positive) becomes an inferior retinal position (y
        /// negative), whose direction is 270 degrees in disc-space: inferior.</para>
        /// </summary>
        public static float FieldPointToDiscAngle(float xDeg, float yDeg)
        {
            float retinaX = -xDeg;
            float retinaY = -yDeg;

            if (Mathf.Abs(retinaX) < 1e-6f && Mathf.Abs(retinaY) < 1e-6f) return 0f;

            float angle = Mathf.Atan2(retinaY, retinaX);
            return Mathf.Repeat(angle, DiscGeometry.TwoPi);
        }

        /// <summary>
        /// How much of a bundle's damage actually reaches this point, 0..1.
        /// Central points are damped because the papillomacular bundle survives
        /// long after the arcuate bundles have gone.
        /// </summary>
        public static float BundleVulnerability(float xDeg, float yDeg)
        {
            float ecc = Mathf.Sqrt(xDeg * xDeg + yDeg * yDeg);
            if (ecc >= PapillomacularRadiusDeg + 5f) return 1f;

            // 0.2 at fixation rising to 1 by 15 degrees.
            float t = Mathf.Clamp01((ecc - 2f) / (PapillomacularRadiusDeg + 3f));
            return Mathf.Lerp(0.2f, 1f, t);
        }

        /// <summary>
        /// Derives a field from an RNFL profile, which is itself derived from the
        /// nerve head geometry. Nothing here reads the authored spec.
        /// </summary>
        public static VisualFieldResult Analyse(RnflProfile rnfl, Laterality laterality)
        {
            VisualFieldResult result = new VisualFieldResult();
            result.Laterality = laterality;
            result.Points = BuildGrid();

            float deviationSum = 0f;
            int counted = 0;

            for (int i = 0; i < result.Points.Length; i++)
            {
                VisualFieldPoint p = result.Points[i];

                if (p.IsBlindSpot)
                {
                    // An absolute scotoma that is meant to be there. Excluded from
                    // the indices, exactly as the physiological blind spot is
                    // excluded from a real MD.
                    p.MeasuredDb = 0f;
                    p.DeviationDb = 0f;
                    result.Points[i] = p;
                    continue;
                }

                float survival = rnfl == null
                    ? 1f
                    : rnfl.SurvivalAtTheta(FieldPointToDiscAngle(p.XDeg, p.YDeg));

                float loss = MaxLossDb * (1f - survival) * BundleVulnerability(p.XDeg, p.YDeg);

                p.MeasuredDb = Mathf.Max(0f, p.NormalDb - loss);
                p.DeviationDb = p.MeasuredDb - p.NormalDb;

                deviationSum += p.DeviationDb;
                counted++;

                result.Points[i] = p;
            }

            result.MeanDeviationDb = counted > 0 ? deviationSum / counted : 0f;

            // PSD is the scatter of the deviations about their own mean: it is high
            // when loss is LOCALISED and low when the whole field is uniformly
            // depressed. That is what separates early glaucoma from a cataract.
            float varianceSum = 0f;
            for (int i = 0; i < result.Points.Length; i++)
            {
                if (result.Points[i].IsBlindSpot) continue;
                float d = result.Points[i].DeviationDb - result.MeanDeviationDb;
                varianceSum += d * d;
            }

            result.PatternStdDevDb = counted > 0 ? Mathf.Sqrt(varianceSum / counted) : 0f;
            result.Stage = StageFor(result.MeanDeviationDb);
            result.StageLabel = StageLabel(result.Stage);

            return result;
        }

        /// <summary>Hodapp-Parrish-Anderson bands on mean deviation.</summary>
        public static FieldStage StageFor(float meanDeviationDb)
        {
            if (meanDeviationDb >= -2f) return FieldStage.WithinNormalLimits;
            if (meanDeviationDb > -6f) return FieldStage.Mild;
            if (meanDeviationDb >= -12f) return FieldStage.Moderate;
            return FieldStage.Severe;
        }

        public static string StageLabel(FieldStage stage)
        {
            switch (stage)
            {
                case FieldStage.WithinNormalLimits: return "within normal limits";
                case FieldStage.Mild: return "early / mild defect (MD better than -6 dB)";
                case FieldStage.Moderate: return "moderate defect (MD -6 to -12 dB)";
                default: return "severe defect (MD worse than -12 dB)";
            }
        }
    }
}
