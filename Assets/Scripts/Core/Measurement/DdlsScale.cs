// -----------------------------------------------------------------------------
// Ocular -- Spaeth Disc Damage Likelihood Scale.
//
// The DDLS exists because the cup-to-disc ratio is a poor damage measure on its
// own: it says nothing about how much rim is left, and it means different things
// on a small disc and a large one. The DDLS stages the NARROWEST rim instead,
// then shifts the stage by disc size.
//
// The bands below (>=0.4, 0.3-0.39, 0.2-0.29, 0.1-0.19, <0.1, then degrees of
// rim absence) are the published DDLS bands, and the input is the quantity the
// published scale actually defines: the NARROWEST radial rim width divided by
// the disc DIAMETER on that same meridian. OnhMeasurementEngine divides by
// 2 * discRadius for exactly this reason.
//
// Dividing by the radius instead is the natural mistake, and it is silent: every
// ratio comes out twice as large, so a disc the printed chart stages at 3 reports
// as stage 1 and an eye with real damage reads as normal. As a sanity anchor, a
// healthy disc at CDR 0.3 gives rim/diameter = (1 - 0.3) / 2 = 0.35, i.e. stage 2
// -- which is where a normal disc belongs on the DDLS, since stage 1 needs a CDR
// of about 0.2 or less.
// -----------------------------------------------------------------------------

using UnityEngine;
using Ocular.Core.Anatomy;

namespace Ocular.Core.Measurement
{
    public static class DdlsScale
    {
        /// <summary>
        /// Rim width, as a fraction of the disc radius, below which the rim counts
        /// as ABSENT rather than merely thin.
        ///
        /// <para>A ratio and not an absolute width, so the same judgement holds on a
        /// 1.2 mm disc and a 2.6 mm one. It also sits just above the floor the
        /// surface itself imposes: OnhSurface.MaxCupFraction leaves 2% of the RADIUS
        /// as rim even at a total notch, which is 1% of the diameter, so the
        /// threshold has to clear 0.01 for stages 6-10 to be reachable at all.</para>
        /// </summary>
        public const float RimAbsentRatio = 0.015f;

        /// <summary>
        /// Rim absence narrower than this is ignored, degrees.
        ///
        /// <para>One meridian out of the default 720 is half a degree. Without this
        /// floor a rim that merely grazes the absence threshold at a single sample
        /// would jump the stage from 1 to 6 -- a five-stage swing off one sample of
        /// quantisation noise.</para>
        /// </summary>
        public const float MinAbsenceDegrees = 1f;

        /// <summary>
        /// Full DDLS stage: rim ratio bands, overridden by rim absence when there
        /// is any, then shifted by disc size. Result is clamped to 1..10.
        /// </summary>
        public static int Stage(float minRimToDiscRatio, float rimAbsenceDegrees, DiscSizeClass discSize)
        {
            return AdjustForDiscSize(StageForAverageDisc(minRimToDiscRatio, rimAbsenceDegrees), discSize);
        }

        /// <summary>Stage before the disc-size shift, i.e. the stage for an average disc.</summary>
        public static int StageForAverageDisc(float minRimToDiscRatio, float rimAbsenceDegrees)
        {
            // Rim absence supersedes the ratio bands entirely: once the rim is gone
            // somewhere, how thin the rest of it is stops being the question.
            if (rimAbsenceDegrees >= MinAbsenceDegrees)
            {
                if (rimAbsenceDegrees <= 45f) return 6;
                if (rimAbsenceDegrees <= 90f) return 7;
                if (rimAbsenceDegrees <= 180f) return 8;
                if (rimAbsenceDegrees <= 270f) return 9;
                return 10;
            }

            // The published table reads "<45", "46-90", "91-180", "181-270", ">270"
            // and so leaves a one-degree hole at each boundary. The <= chain above
            // closes them upward rather than leaving a stage undefined.

            if (minRimToDiscRatio >= 0.4f) return 1;
            if (minRimToDiscRatio >= 0.3f) return 2;
            if (minRimToDiscRatio >= 0.2f) return 3;
            if (minRimToDiscRatio >= 0.1f) return 4;
            return 5;
        }

        /// <summary>
        /// Shifts a stage for disc size, clamped to 1..10.
        ///
        /// <para>Up one for a small disc, down one for a large one. The same rim
        /// ratio on a small disc means less surviving axonal tissue in absolute
        /// terms, so it is worse damage; on a large disc it is better than it
        /// looks. This shift is the whole reason the DDLS beats a bare CDR.</para>
        /// </summary>
        public static int AdjustForDiscSize(int stage, DiscSizeClass discSize)
        {
            int shifted = stage;

            if (discSize == DiscSizeClass.Small) shifted += 1;
            else if (discSize == DiscSizeClass.Large) shifted -= 1;

            return Mathf.Clamp(shifted, 1, 10);
        }

        /// <summary>Plain-language label for a stage. Out-of-range stages are clamped first.</summary>
        public static string Label(int stage)
        {
            switch (Mathf.Clamp(stage, 1, 10))
            {
                case 1: return "normal rim, 0.4 or wider";
                case 2: return "rim 0.3 to 0.39";
                case 3: return "rim 0.2 to 0.29";
                case 4: return "rim 0.1 to 0.19";
                case 5: return "rim thinner than 0.1, still present all round";
                case 6: return "rim absent for less than 45 degrees";
                case 7: return "rim absent for 46 to 90 degrees";
                case 8: return "rim absent for 91 to 180 degrees";
                case 9: return "rim absent for 181 to 270 degrees";
                default: return "rim absent for more than 270 degrees";
            }
        }
    }
}
