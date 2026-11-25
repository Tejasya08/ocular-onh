// -----------------------------------------------------------------------------
// Ocular -- proving the numbers are MEASURED, not echoed.
//
// RoundTrip_SlopeContour_RecoversAuthoredVerticalCdr is the test this whole
// project rests on. If the measurement engine ever starts reading
// OpticNerveHeadSpec instead of sampling the surface, every other test here would
// still pass and this one would still pass -- but it would pass for the wrong
// reason, so it is paired with the two-method disagreement test below. Those two
// together can only both hold if the engine really is sampling geometry.
// -----------------------------------------------------------------------------

using NUnit.Framework;
using UnityEngine;
using Ocular.Core.Anatomy;
using Ocular.Core.Measurement;

namespace Ocular.Tests
{
    public class OnhMeasurementTests
    {
        /// <summary>
        /// A disc whose rim is untouched by sector scales or a notch, so the cup is
        /// exactly the authored CDR ellipse and the round trip has a known answer.
        /// </summary>
        private static OpticNerveHeadSpec Uniform(float cdr)
        {
            OpticNerveHeadSpec s = OpticNerveHeadSpec.Healthy();
            s.CupToDiscVertical = cdr;
            s.CupToDiscHorizontal = cdr;
            s.RimScaleInferior = 1f;
            s.RimScaleSuperior = 1f;
            s.RimScaleNasal = 1f;
            s.RimScaleTemporal = 1f;
            s.NotchDepth = 0f;
            return s;
        }

        [Test]
        public void RoundTrip_SlopeContour_RecoversAuthoredVerticalCdr(
            [Values(0.3f, 0.45f, 0.55f, 0.7f)] float authored)
        {
            OnhSurface surface = new OnhSurface(Uniform(authored));
            OnhMetrics m = OnhMeasurementEngine.Measure(surface, MeasurementMethod.SlopeContour);

            Assert.AreEqual(authored, m.VerticalCdr, 0.02f,
                "Slope-contour CDR must recover the authored ratio from the geometry alone.");
        }

        [Test]
        public void ReferencePlane_ReadsSmallerCupThanSlopeContour()
        {
            OnhSurface surface = new OnhSurface(Uniform(0.55f));

            OnhMetrics contour = OnhMeasurementEngine.Measure(surface, MeasurementMethod.SlopeContour);
            OnhMetrics plane = OnhMeasurementEngine.Measure(surface, MeasurementMethod.ReferencePlane);

            // Direction asserted explicitly: the plane sits 50 microns below the
            // retinal surface, so it only starts counting once the wall has already
            // descended that far. A smaller cup is correct, not a bug.
            Assert.Less(plane.VerticalCdr, contour.VerticalCdr - 0.005f,
                "The reference plane must read a smaller cup than the geometric contour.");

            Assert.IsTrue(plane.CupBelowReferencePlane,
                "A 0.28mm cup is comfortably deeper than the 50 micron plane offset.");
        }

        [Test]
        public void ShallowCup_FlagsInsteadOfDividingByZero()
        {
            OpticNerveHeadSpec s = Uniform(0.5f);
            s.CupDepthMm = 0.04f;   // shallower than the 50 micron reference offset

            OnhMetrics m = OnhMeasurementEngine.Measure(new OnhSurface(s), MeasurementMethod.ReferencePlane);

            Assert.IsFalse(m.CupBelowReferencePlane,
                "A cup shallower than the plane offset must be flagged, not silently measured.");
            Assert.AreEqual(0f, m.CupAreaMm2, 1e-6f, "An unfound cup reports zero area.");
        }

        [Test]
        public void Areas_AreSelfConsistent()
        {
            OnhMetrics m = OnhMeasurementEngine.Measure(
                new OnhSurface(Uniform(0.6f)), MeasurementMethod.SlopeContour);

            Assert.AreEqual(m.DiscAreaMm2, m.CupAreaMm2 + m.RimAreaMm2, 1e-4f,
                "Disc area must be exactly cup plus rim.");

            Assert.AreEqual(m.AreaCdr, m.LinearCdr * m.LinearCdr, 1e-5f,
                "Linear CDR is the square root of the area CDR by definition.");
        }

        [Test]
        public void DiscArea_MatchesTheEllipseItWasBuiltFrom()
        {
            OpticNerveHeadSpec s = Uniform(0.4f);
            OnhMetrics m = OnhMeasurementEngine.Measure(new OnhSurface(s), MeasurementMethod.SlopeContour);

            // pi * a * b for the authored ellipse. This is what catches a polar
            // integral that used r instead of 0.5 * r^2.
            float expected = Mathf.PI
                           * (s.DiscDiameterHorizontalMm * 0.5f)
                           * (s.DiscDiameterVerticalMm * 0.5f);

            Assert.AreEqual(expected, m.DiscAreaMm2, 1e-3f,
                "Polar area integral must recover the ellipse area.");
        }

        [Test]
        public void VerticalCdr_UsesDiameters_NotASingleRadius()
        {
            // A cup pushed hard inferiorly is no longer concentric with the disc.
            // Averaging two separate half-ratios and summing two half-diameters give
            // different answers; only the summed diameter is a true CDR.
            OpticNerveHeadSpec s = Uniform(0.5f);
            s.RimScaleInferior = 0.4f;

            OnhSurface surface = new OnhSurface(s);
            OnhMetrics m = OnhMeasurementEngine.Measure(surface, MeasurementMethod.SlopeContour);

            float sup = DiscGeometry.SectorCentre(DiscSector.Superior);
            float inf = DiscGeometry.SectorCentre(DiscSector.Inferior);

            float expected = (surface.CupRadiusMm(sup) + surface.CupRadiusMm(inf))
                           / (surface.DiscRadiusMm(sup) + surface.DiscRadiusMm(inf));

            Assert.AreEqual(expected, m.VerticalCdr, 0.02f,
                "Vertical CDR must be the ratio of summed diameters.");
        }

        [Test]
        public void Isnt_HoldsOnAHealthyDisc()
        {
            OnhMetrics m = OnhMeasurementEngine.Measure(
                new OnhSurface(OpticNerveHeadSpec.Healthy()), MeasurementMethod.SlopeContour);

            Assert.IsTrue(m.IsntSatisfied,
                "The healthy preset encodes I > S > N > T, so ISNT must hold: " + m.IsntDetail);

            float[] rim = m.RimWidthBySectorMm;
            Assert.Greater(rim[(int)DiscSector.Inferior], rim[(int)DiscSector.Superior]);
            Assert.Greater(rim[(int)DiscSector.Nasal], rim[(int)DiscSector.Temporal]);
        }

        [Test]
        public void Isnt_BreaksWhenTheInferiorRimIsThinned_AndNamesTheInequality()
        {
            OpticNerveHeadSpec s = OpticNerveHeadSpec.Healthy();
            s.RimScaleInferior = 0.45f;   // now thinner than superior

            OnhMetrics m = OnhMeasurementEngine.Measure(new OnhSurface(s), MeasurementMethod.SlopeContour);

            Assert.IsFalse(m.IsntSatisfied, "Inferior thinner than superior must break ISNT.");
            StringAssert.Contains("inferior", m.IsntDetail.ToLowerInvariant());
            StringAssert.Contains("superior", m.IsntDetail.ToLowerInvariant());
        }

        [Test]
        public void NarrowestRim_IsFoundAtTheNotch()
        {
            OpticNerveHeadSpec s = Uniform(0.6f);
            s.NotchAngleDeg = 270f;
            s.NotchDepth = 0.8f;
            s.NotchWidthDeg = 14f;

            OnhMetrics m = OnhMeasurementEngine.Measure(new OnhSurface(s), MeasurementMethod.SlopeContour);

            float delta = Mathf.Abs(Mathf.DeltaAngle(m.MinRimAngleDeg, 270f));
            Assert.Less(delta, 25f,
                "The narrowest rim must be located at the notch, not somewhere else. Found " + m.MinRimAngleDeg);
        }

        [Test]
        public void MinRimToDiscRatio_IsAgainstDiameter_NotRadius()
        {
            // A uniform 0.5 CDR leaves rim = 0.25 of the diameter on every meridian.
            // Against the RADIUS it would read 0.5, which would stage two bands too
            // favourably on the DDLS.
            OnhMetrics m = OnhMeasurementEngine.Measure(
                new OnhSurface(Uniform(0.5f)), MeasurementMethod.SlopeContour);

            Assert.AreEqual(0.25f, m.MinRimToDiscRatio, 0.015f,
                "Rim ratio must be rim width over disc diameter.");
        }
    }
}
