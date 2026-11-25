// -----------------------------------------------------------------------------
// Ocular -- everything one measurement pass produced.
//
// Written once by OnhMeasurementEngine and read by the HUD, the overlays and the
// EditMode tests. Nothing in here is copied from OpticNerveHeadSpec: every field
// was derived from the surface, so a test that compares a metric against the
// authored value is genuinely testing the round trip.
//
// A class rather than a struct because it carries arrays; passing it by value
// would copy the header on every HUD call and still share the arrays, which is
// the worst of both.
// -----------------------------------------------------------------------------

using System.Text;
using Ocular.Core.Anatomy;

namespace Ocular.Core.Measurement
{
    public sealed class OnhMetrics
    {
        // --- Cup-to-disc ratios ----------------------------------------------

        /// <summary>Vertical CDR: the figure that gets quoted clinically.</summary>
        public float VerticalCdr;

        public float HorizontalCdr;

        /// <summary>Cup area over disc area. Falls faster than the linear ratios.</summary>
        public float AreaCdr;

        /// <summary>sqrt(AreaCdr). Comparable in magnitude to the linear ratios above.</summary>
        public float LinearCdr;

        // --- Areas, mm^2 -------------------------------------------------------

        public float DiscAreaMm2;
        public float CupAreaMm2;

        /// <summary>Disc area minus cup area. The tissue that is actually left.</summary>
        public float RimAreaMm2;

        // --- Disc dimensions, mm ----------------------------------------------

        public float DiscDiameterVerticalMm;
        public float DiscDiameterHorizontalMm;

        /// <summary>
        /// Small / average / large. A CDR cannot be read without it: a large disc
        /// carries a large cup on a perfectly normal amount of rim.
        /// </summary>
        public DiscSizeClass DiscSize;

        // --- Rim width, mm, indexed by DiscSector ------------------------------

        /// <summary>Mean rim width per sector. This is what the ISNT rule is applied to.</summary>
        public float[] RimWidthBySectorMm = new float[4];

        /// <summary>Narrowest rim width found in each sector. Where a notch shows up.</summary>
        public float[] MinRimWidthBySectorMm = new float[4];

        // --- ISNT --------------------------------------------------------------

        /// <summary>True when mean rim width runs Inferior >= Superior >= Nasal >= Temporal.</summary>
        public bool IsntSatisfied;

        /// <summary>The first inequality that failed, in words. "Obeyed" when none did.</summary>
        public string IsntDetail = string.Empty;

        // --- Narrowest rim -----------------------------------------------------

        /// <summary>
        /// Smallest value of (discRadius - cupRadius) / discRadius over the whole
        /// circumference. Drives the DDLS stage.
        /// </summary>
        public float MinRimToDiscRatio;

        /// <summary>Disc-space angle of that narrowest rim, degrees (0 = temporal).</summary>
        public float MinRimAngleDeg;

        /// <summary>
        /// Angular extent over which the rim is effectively gone, degrees. Zero on
        /// any disc that still has rim all the way round.
        /// </summary>
        public float RimAbsenceDegrees;

        // --- DDLS --------------------------------------------------------------

        public int DdlsStage;
        public string DdlsLabel = string.Empty;

        // --- Provenance --------------------------------------------------------

        public MeasurementMethod Method;

        /// <summary>Meridians sampled. Recorded because the areas are Riemann sums over them.</summary>
        public int SampleCount;

        /// <summary>
        /// False when the cup floor never reaches the reference plane, i.e. the cup
        /// is shallower than the plane's own 50 micron offset. The reported cup is
        /// then zero by definition rather than by measurement, and the HUD needs to
        /// say so instead of showing a confident 0.00 CDR.
        /// </summary>
        public bool CupBelowReferencePlane;

        /// <summary>Human-readable block for the HUD.</summary>
        public string ToReport()
        {
            StringBuilder sb = new StringBuilder(512);

            sb.Append("ONH MEASUREMENT  (")
              .Append(Method == MeasurementMethod.ReferencePlane ? "reference plane" : "slope contour")
              .Append(", ").Append(SampleCount).Append(" meridians)\n");

            sb.Append("Disc      ").Append(DiscDiameterVerticalMm.ToString("0.00"))
              .Append(" V x ").Append(DiscDiameterHorizontalMm.ToString("0.00"))
              .Append(" H mm   ").Append(DiscSize).Append('\n');

            sb.Append("Area      disc ").Append(DiscAreaMm2.ToString("0.000"))
              .Append("   cup ").Append(CupAreaMm2.ToString("0.000"))
              .Append("   rim ").Append(RimAreaMm2.ToString("0.000")).Append(" mm2\n");

            sb.Append("CDR       V ").Append(VerticalCdr.ToString("0.00"))
              .Append("   H ").Append(HorizontalCdr.ToString("0.00"))
              .Append("   area ").Append(AreaCdr.ToString("0.00"))
              .Append("   linear ").Append(LinearCdr.ToString("0.00")).Append('\n');

            sb.Append("Rim mean  I ").Append(RimWidthBySectorMm[(int)DiscSector.Inferior].ToString("0.00"))
              .Append("   S ").Append(RimWidthBySectorMm[(int)DiscSector.Superior].ToString("0.00"))
              .Append("   N ").Append(RimWidthBySectorMm[(int)DiscSector.Nasal].ToString("0.00"))
              .Append("   T ").Append(RimWidthBySectorMm[(int)DiscSector.Temporal].ToString("0.00"))
              .Append(" mm\n");

            sb.Append("Rim min   I ").Append(MinRimWidthBySectorMm[(int)DiscSector.Inferior].ToString("0.00"))
              .Append("   S ").Append(MinRimWidthBySectorMm[(int)DiscSector.Superior].ToString("0.00"))
              .Append("   N ").Append(MinRimWidthBySectorMm[(int)DiscSector.Nasal].ToString("0.00"))
              .Append("   T ").Append(MinRimWidthBySectorMm[(int)DiscSector.Temporal].ToString("0.00"))
              .Append(" mm\n");

            sb.Append("ISNT      ").Append(IsntSatisfied ? "obeyed" : "BROKEN")
              .Append("   ").Append(IsntDetail).Append('\n');

            sb.Append("Narrowest rim/disc ").Append(MinRimToDiscRatio.ToString("0.00"))
              .Append(" at ").Append(MinRimAngleDeg.ToString("0")).Append(" deg");
            if (RimAbsenceDegrees > 0f)
            {
                sb.Append("   rim absent over ").Append(RimAbsenceDegrees.ToString("0")).Append(" deg");
            }
            sb.Append('\n');

            sb.Append("DDLS      stage ").Append(DdlsStage).Append(" - ").Append(DdlsLabel).Append('\n');

            if (Method == MeasurementMethod.ReferencePlane && !CupBelowReferencePlane)
            {
                sb.Append("NOTE      cup floor never reaches the reference plane; ")
                  .Append("cup reported as zero. Try the slope contour method.\n");
            }

            return sb.ToString();
        }
    }
}
