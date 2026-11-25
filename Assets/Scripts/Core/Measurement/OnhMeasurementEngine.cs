// -----------------------------------------------------------------------------
// Ocular -- reading glaucoma parameters back out of the generated surface.
//
// Nothing here consults OpticNerveHeadSpec for an answer. Every number is found
// by sampling OnhSurface, exactly as an instrument samples a real nerve head,
// which is what makes the discrepancy between the two methods -- and between
// either method and the authored ground truth -- something the user can see and
// reason about rather than something the code hides.
//
// Millimetres and radians in, millimetres and degrees out.
// -----------------------------------------------------------------------------

using UnityEngine;
using Ocular.Core.Anatomy;
using Ocular.Core.Diagnostics;

namespace Ocular.Core.Measurement
{
    public static class OnhMeasurementEngine
    {
        /// <summary>
        /// How far below the temporal retinal surface the reference plane sits, mm.
        /// 50 microns is the Heidelberg standard offset.
        /// </summary>
        public const float ReferencePlaneOffsetMm = 0.050f;

        /// <summary>
        /// Wall steepness that marks the cup margin for the contour method,
        /// mm of depth per mm of radius. Around 0.5 the rim roll-over is
        /// unambiguous: the rim itself never exceeds a few hundredths, and the cup
        /// wall is several times steeper than the threshold at its top.
        /// </summary>
        public const float SlopeThreshold = 0.5f;

        /// <summary>Radial steps used to walk in from the disc margin.</summary>
        public const int RadialSamples = 256;

        // Bisection halves the bracket each time, so 28 steps resolve the crossing
        // to about a millionth of the disc radius -- far below anything that
        // matters, and still cheaper than a linear scan.
        private const int BisectionSteps = 28;

        /// <summary>
        /// Rim-width difference below which two sectors are called a TIE, mm.
        ///
        /// <para>This is a RESOLUTION threshold, not a numerical one, and the
        /// difference matters. It was 1e-4 mm -- a tenth of a micron -- which is a
        /// guard against float noise and nothing else. At that setting a rim half
        /// a micron thinner inferiorly than superiorly reported ISNT BROKEN, in a
        /// readout that prints both numbers to two decimals, so the user saw
        /// "inferior rim 0.66 mm is thinner than superior 0.66 mm" and had no way
        /// to tell it was not a bug. Observed on a real fundus photograph.</para>
        ///
        /// <para>20 microns is comfortably below anything a clinician could
        /// resolve at the slit lamp or a planimeter could repeat, and far below
        /// the interobserver spread on where the rim margin even is -- so a gap
        /// smaller than this is not an ordering, it is noise. Calling it a tie
        /// reports ISNT as satisfied, which is the correct reading of two sectors
        /// that cannot be told apart.</para>
        /// </summary>
        private const float IsntEpsilonMm = 0.02f;

        /// <summary>Height of the reference plane for this surface, mm (negative).</summary>
        public static float ReferencePlaneZMm(OnhSurface surface)
        {
            // Anchored at the TEMPORAL margin because that is where the retina is
            // flattest and least disturbed by the arcuate bundles; the standard
            // picks it for reproducibility, not because temporal is special
            // clinically.
            float temporalMarginR = surface.DiscRadiusMm(0f);
            return surface.HeightMm(temporalMarginR, 0f) - ReferencePlaneOffsetMm;
        }

        /// <summary>
        /// Cup margin radius at one meridian by the selected method, mm.
        /// <paramref name="cupFound"/> is false when the method finds no cup at
        /// this angle, in which case the return is 0.
        /// </summary>
        public static float CupRadiusAt(OnhSurface surface, MeasurementMethod method,
                                        float theta, float referencePlaneZMm, out bool cupFound)
        {
            float discR = surface.DiscRadiusMm(theta);
            float geometricCupR = surface.CupRadiusMm(theta);
            return CupRadiusAt(surface, method, theta, referencePlaneZMm, discR, geometricCupR, out cupFound);
        }

        private static float CupRadiusAt(OnhSurface surface, MeasurementMethod method,
                                         float theta, float referencePlaneZMm,
                                         float discR, float geometricCupR, out bool cupFound)
        {
            if (method == MeasurementMethod.ReferencePlane)
            {
                return ReferencePlaneCupRadius(surface, theta, referencePlaneZMm, discR, geometricCupR, out cupFound);
            }

            return SlopeContourCupRadius(surface, theta, discR, geometricCupR, out cupFound);
        }

        /// <summary>
        /// Largest radius inside the disc whose surface lies below the reference
        /// plane. The height field descends monotonically from the disc margin to
        /// the cup floor, so the crossing is unique and can be bisected.
        /// </summary>
        private static float ReferencePlaneCupRadius(OnhSurface surface, float theta, float planeZ,
                                                     float discR, float geometricCupR, out bool cupFound)
        {
            float deepest = surface.HeightMm(0f, theta, discR, geometricCupR);
            if (deepest >= planeZ)
            {
                // The floor never gets down to the plane. Happens whenever the cup
                // is shallower than the plane's own 50 micron offset. Reporting a
                // zero cup is the honest answer; dividing by a cup that was never
                // found is not.
                cupFound = false;
                return 0f;
            }

            float atMargin = surface.HeightMm(discR, theta, discR, geometricCupR);
            if (atMargin < planeZ)
            {
                // Defensive: only reachable if the retina itself sat below the
                // plane, which the temporal anchor rules out.
                cupFound = true;
                return discR;
            }

            float lo = 0f;      // below the plane
            float hi = discR;   // above the plane
            for (int i = 0; i < BisectionSteps; i++)
            {
                float mid = 0.5f * (lo + hi);
                if (surface.HeightMm(mid, theta, discR, geometricCupR) < planeZ) lo = mid;
                else hi = mid;
            }

            cupFound = true;
            return 0.5f * (lo + hi);
        }

        /// <summary>
        /// First radius, walking inward from the disc margin, where the wall tips
        /// steeper than the threshold. The rim meets the retina tangentially and
        /// stays shallow all the way across, so the first steep sample is the cup
        /// edge and not the disc margin.
        /// </summary>
        private static float SlopeContourCupRadius(OnhSurface surface, float theta,
                                                   float discR, float geometricCupR, out bool cupFound)
        {
            float step = discR / RadialSamples;

            for (int i = 1; i <= RadialSamples; i++)
            {
                float r = discR - i * step;
                if (r <= 0f) break;

                if (Mathf.Abs(surface.SlopeAt(r, theta, discR, geometricCupR)) > SlopeThreshold)
                {
                    cupFound = true;
                    return r;
                }
            }

            // A saucerised cup with no steep wall anywhere. Clinically that is a
            // disc whose cup margin genuinely cannot be called at the slit lamp.
            cupFound = false;
            return 0f;
        }

        /// <summary>
        /// One full measurement pass. <paramref name="samples"/> meridians are
        /// swept uniformly in disc-space.
        /// </summary>
        public static OnhMetrics Measure(OnhSurface surface, MeasurementMethod method, int samples = 720)
        {
            OnhMetrics m = new OnhMetrics();
            m.Method = method;

            if (surface == null)
            {
                OLog.Error(LogCat.Measure, "Measure called with a null surface.");
                m.IsntDetail = "no surface";
                m.DdlsLabel = DdlsScale.Label(1);
                m.DdlsStage = 1;
                return m;
            }

            // Four meridians is not a circle; below this the Riemann sums for area
            // are meaningless rather than merely coarse.
            samples = Mathf.Max(8, samples);
            m.SampleCount = samples;

            float dTheta = DiscGeometry.TwoPi / samples;
            float planeZ = ReferencePlaneZMm(surface);

            float discArea = 0f;
            float cupArea = 0f;

            float[] rimSum = new float[4];
            int[] rimCount = new int[4];
            float[] rimMin = new float[4];
            for (int s = 0; s < 4; s++) rimMin[s] = float.MaxValue;

            float minRimRatio = float.MaxValue;
            float minRimAngleRad = 0f;
            int absentSamples = 0;
            float deepestMm = 0f;

            for (int i = 0; i < samples; i++)
            {
                float theta = i * dTheta;

                float discR = surface.DiscRadiusMm(theta);
                float geometricCupR = surface.CupRadiusMm(theta);

                bool cupFound;
                float cupR = CupRadiusAt(surface, method, theta, planeZ, discR, geometricCupR, out cupFound);

                // Tracked independently of the method in use, so the HUD can still
                // explain a zero reference-plane cup while the contour method is on
                // screen. Height at r = 0 is the cup floor.
                float floorMm = surface.HeightMm(0f, theta, discR, geometricCupR);
                if (floorMm < deepestMm) deepestMm = floorMm;

                discArea += 0.5f * discR * discR * dTheta;
                cupArea += 0.5f * cupR * cupR * dTheta;

                float rimWidth = Mathf.Max(0f, discR - cupR);

                // Rim width over disc DIAMETER, which is how the DDLS defines its
                // input. Dividing by the radius instead would double every ratio and
                // shift a case about two stages too favourably -- a disc the printed
                // chart calls stage 3 would report as stage 1.
                float rimRatio = discR > 1e-6f ? rimWidth / (2f * discR) : 0f;

                int sector = (int)DiscGeometry.SectorAt(theta);
                rimSum[sector] += rimWidth;
                rimCount[sector]++;
                if (rimWidth < rimMin[sector]) rimMin[sector] = rimWidth;

                if (rimRatio < minRimRatio)
                {
                    minRimRatio = rimRatio;
                    minRimAngleRad = theta;
                }

                if (rimRatio < DdlsScale.RimAbsentRatio) absentSamples++;
            }

            m.CupBelowReferencePlane = deepestMm < planeZ;

            if (method == MeasurementMethod.ReferencePlane && !m.CupBelowReferencePlane)
            {
                OLog.Warn(LogCat.Measure,
                    "Cup floor never reaches the reference plane; reporting a zero cup.");
            }

            // --- Areas ---------------------------------------------------------
            m.DiscAreaMm2 = discArea;
            m.CupAreaMm2 = cupArea;
            m.RimAreaMm2 = Mathf.Max(0f, discArea - cupArea);
            m.AreaCdr = discArea > 1e-9f ? Mathf.Clamp01(cupArea / discArea) : 0f;
            m.LinearCdr = Mathf.Sqrt(m.AreaCdr);

            // --- Linear ratios at the cardinal meridians -----------------------
            // Vertical CDR is the sum of the two half-diameters rather than the
            // mean of two separate ratios: the cup is not concentric with the disc
            // once a notch is in play, and summing keeps it a true diameter ratio.
            m.VerticalCdr = CardinalRatio(surface, method, planeZ, DiscGeometry.HalfPi, 3f * DiscGeometry.HalfPi);
            m.HorizontalCdr = CardinalRatio(surface, method, planeZ, 0f, Mathf.PI);

            m.DiscDiameterVerticalMm = surface.DiscRadiusMm(DiscGeometry.HalfPi)
                                     + surface.DiscRadiusMm(3f * DiscGeometry.HalfPi);
            m.DiscDiameterHorizontalMm = surface.DiscRadiusMm(0f) + surface.DiscRadiusMm(Mathf.PI);
            m.DiscSize = DiscGeometry.ClassifyDiscSize(m.DiscDiameterVerticalMm);

            // --- Rim by sector --------------------------------------------------
            for (int s = 0; s < 4; s++)
            {
                m.RimWidthBySectorMm[s] = rimCount[s] > 0 ? rimSum[s] / rimCount[s] : 0f;
                m.MinRimWidthBySectorMm[s] = rimCount[s] > 0 ? rimMin[s] : 0f;
            }

            EvaluateIsnt(m);

            // --- Narrowest rim and DDLS ----------------------------------------
            m.MinRimToDiscRatio = minRimRatio == float.MaxValue ? 0f : minRimRatio;
            m.MinRimAngleDeg = Mathf.Repeat(minRimAngleRad * Mathf.Rad2Deg, 360f);
            m.RimAbsenceDegrees = absentSamples * (360f / samples);

            m.DdlsStage = DdlsScale.Stage(m.MinRimToDiscRatio, m.RimAbsenceDegrees, m.DiscSize);
            m.DdlsLabel = DdlsScale.Label(m.DdlsStage);

            return m;
        }

        /// <summary>Diameter ratio across two opposed meridians.</summary>
        private static float CardinalRatio(OnhSurface surface, MeasurementMethod method,
                                           float planeZ, float thetaA, float thetaB)
        {
            bool foundA, foundB;
            float cupA = CupRadiusAt(surface, method, thetaA, planeZ, out foundA);
            float cupB = CupRadiusAt(surface, method, thetaB, planeZ, out foundB);

            float discSum = surface.DiscRadiusMm(thetaA) + surface.DiscRadiusMm(thetaB);
            if (discSum <= 1e-6f) return 0f;

            return Mathf.Clamp01((cupA + cupB) / discSum);
        }

        /// <summary>
        /// The ISNT rule: a healthy rim is widest inferiorly, then superiorly, then
        /// nasally, and thinnest temporally. Glaucoma attacks the vertical poles
        /// first, so the inferior-superior end of the chain is usually where it
        /// breaks, and naming WHICH inequality failed is more useful than a flag.
        /// </summary>
        private static void EvaluateIsnt(OnhMetrics m)
        {
            float inferior = m.RimWidthBySectorMm[(int)DiscSector.Inferior];
            float superior = m.RimWidthBySectorMm[(int)DiscSector.Superior];
            float nasal = m.RimWidthBySectorMm[(int)DiscSector.Nasal];
            float temporal = m.RimWidthBySectorMm[(int)DiscSector.Temporal];

            // The epsilon keeps a numerically dead heat between two sectors from
            // being reported as a broken rule.
            if (inferior + IsntEpsilonMm < superior)
            {
                m.IsntSatisfied = false;
                m.IsntDetail = Failure("inferior", inferior, "superior", superior);
                return;
            }

            if (superior + IsntEpsilonMm < nasal)
            {
                m.IsntSatisfied = false;
                m.IsntDetail = Failure("superior", superior, "nasal", nasal);
                return;
            }

            if (nasal + IsntEpsilonMm < temporal)
            {
                m.IsntSatisfied = false;
                m.IsntDetail = Failure("nasal", nasal, "temporal", temporal);
                return;
            }

            m.IsntSatisfied = true;
            m.IsntDetail = "I >= S >= N >= T";
        }

        /// <summary>
        /// Three decimals, and the gap stated outright.
        ///
        /// <para>Two decimals is the right precision for a rim width and the wrong
        /// precision for a COMPARISON of two of them: a genuine 30-micron
        /// violation prints as "0.66 mm is thinner than 0.66 mm", which reads as a
        /// broken program rather than a broken rule. Showing the margin means the
        /// reader can see at a glance whether the violation is marginal or
        /// gross -- which is most of what they want to know.</para>
        /// </summary>
        private static string Failure(string thinName, float thinMm, string thickName, float thickMm)
        {
            return thinName + " rim " + thinMm.ToString("0.000")
                 + " mm is thinner than " + thickName + " " + thickMm.ToString("0.000")
                 + " mm (by " + ((thickMm - thinMm) * 1000f).ToString("0") + " um)";
        }
    }
}
