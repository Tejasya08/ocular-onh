// -----------------------------------------------------------------------------
// Ocular -- the three failures a real fundus photograph exposed.
//
// Every one of them produced a confident, plausible, WRONG readout rather than
// an error, which is the only kind of bug this project really has to fear. They
// are pinned here because all three would be easy to reintroduce by tuning a
// constant.
//
//   1. ISNT reported BROKEN on rims that differ by less than a micron.
//   2. A cropped photograph was scaled as though it were a full camera field,
//      shrinking every millimetre and mislabelling a normal disc "Small".
//   3. A hand trace could not be fed back in when the auto-trace failed.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Ocular.Core.Anatomy;
using Ocular.Core.Imaging;
using Ocular.Core.Measurement;

namespace Ocular.Tests
{
    public class RealImageFixTests
    {
        // =====================================================================
        // 1. ISNT must not break on a tie
        // =====================================================================

        /// <summary>
        /// A perfectly circular disc with a concentric circular cup has the same
        /// rim width in all four sectors. That is a TIE, and ISNT is satisfied.
        ///
        /// <para>With the old 0.0001 mm epsilon this came down to whichever way
        /// float noise happened to fall, and when it fell the wrong way the
        /// readout said "inferior rim 0.66 mm is thinner than superior 0.66 mm" --
        /// a false abnormality, printed to two decimals so the two numbers it was
        /// comparing looked identical.</para>
        /// </summary>
        [Test]
        public void Isnt_UniformRim_IsSatisfied_NotBrokenByNoise()
        {
            OpticNerveHeadSpec s = OpticNerveHeadSpec.Healthy();

            s.DiscDiameterVerticalMm = 1.80f;
            s.DiscDiameterHorizontalMm = 1.80f;
            s.CupToDiscVertical = 0.50f;
            s.CupToDiscHorizontal = 0.50f;
            s.RimScaleInferior = 1f;
            s.RimScaleSuperior = 1f;
            s.RimScaleNasal = 1f;
            s.RimScaleTemporal = 1f;
            s.NotchDepth = 0f;

            OnhMetrics m = OnhMeasurementEngine.Measure(
                new OnhSurface(s), MeasurementMethod.SlopeContour);

            Assert.That(m.IsntSatisfied, Is.True,
                "A uniform rim was reported as violating ISNT: " + m.IsntDetail);
        }

        /// <summary>A real violation must still be caught, or the fix is just a mute button.</summary>
        [Test]
        public void Isnt_GenuinelyThinInferiorRim_IsStillBroken()
        {
            OpticNerveHeadSpec s = OpticNerveHeadSpec.Healthy();

            s.RimScaleInferior = 0.45f;   // far thinner than superior
            s.RimScaleSuperior = 1.2f;
            s.NotchDepth = 0f;

            OnhMetrics m = OnhMeasurementEngine.Measure(
                new OnhSurface(s), MeasurementMethod.SlopeContour);

            Assert.That(m.IsntSatisfied, Is.False, "A markedly thin inferior rim passed ISNT.");
            Assert.That(m.IsntDetail, Does.Contain("inferior"));

            // The message has to make the comparison legible, which two decimals
            // did not: it must quote the gap, not two numbers that round alike.
            Assert.That(m.IsntDetail, Does.Contain("um"),
                "The failure message does not state the margin: " + m.IsntDetail);
        }

        // =====================================================================
        // 2. Cropped images must not be scaled from the camera field
        // =====================================================================

        private static FundusImage Frame(bool cropped)
        {
            const int Size = 256;

            FundusImage image = new FundusImage(Size, Size);
            Vector2 centre = new Vector2(Size * 0.5f, Size * 0.5f);

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    bool lit = cropped || Vector2.Distance(new Vector2(x, y), centre) <= Size * 0.45f;

                    if (lit) image.SetPixel(x, y, 0.75f, 0.45f, 0.30f);
                    else image.SetPixel(x, y, 0.01f, 0.01f, 0.01f);
                }
            }

            return image;
        }

        [Test]
        public void Framing_FullFundusCircle_IsNotCropped()
        {
            FundusFraming framing = Frame(false).MeasureFraming();

            Assert.That(framing.LooksCropped, Is.False,
                "A circle with dark corners was mistaken for a crop.");

            // A circle inscribed in a square leaves about 1 - pi/4 of it dark.
            Assert.That(framing.DarkFraction, Is.GreaterThan(0.15f));
        }

        [Test]
        public void Framing_EdgeToEdgeImage_IsDetectedAsCropped()
        {
            FundusFraming framing = Frame(true).MeasureFraming();

            Assert.That(framing.LooksCropped, Is.True,
                "An image with no dark surround was treated as a full camera field. "
                + "Every millimetre it reports would be wrong.");

            Assert.That(framing.Explain(), Does.Contain("CROPPED"));
        }

        /// <summary>
        /// The whole point of detecting a crop: the calibration it forces must
        /// declare itself circular, so disc size is reported as assumed rather
        /// than measured.
        /// </summary>
        [Test]
        public void CroppedImage_ForcesACalibrationThatAdmitsItIsAssumed()
        {
            ImageCalibration assumed = ImageCalibration.FromAssumedDiscDiameter(300f, 1.8f);

            Assert.That(assumed.IsCircular, Is.True);

            OnhSurface surface = new OnhSurface(OpticNerveHeadSpec.Healthy());

            float[] discR = new float[PolarContour.DefaultSamples];
            float[] cupR = new float[PolarContour.DefaultSamples];

            for (int i = 0; i < discR.Length; i++)
            {
                float t = (i + 0.5f) / discR.Length * DiscGeometry.TwoPi;
                discR[i] = surface.DiscRadiusMm(t);
                cupR[i] = surface.CupRadiusMm(t);
            }

            OnhFitInputs inputs = new OnhFitInputs();
            inputs.Disc = PolarContour.FromRadii(discR, Vector2.zero);
            inputs.Cup = PolarContour.FromRadii(cupR, Vector2.zero);
            inputs.CalibrationIsCircular = assumed.IsCircular;

            OnhFitReport report = OnhSpecFitter.Fit(inputs);

            Assert.That(report.DiscDiameterIsMeasured, Is.False,
                "A cropped-image fit still claimed to have measured disc size.");
        }

        // =====================================================================
        // 3. A hand trace must be usable as input
        // =====================================================================

        /// <summary>
        /// Points picked in the 3D view arrive already in disc-space millimetres,
        /// so they must NOT be put through the calibration or the laterality
        /// mirror a second time.
        /// </summary>
        [Test]
        public void TracedContour_InMillimetres_RecoversItsOwnDiameters()
        {
            List<Vector2> ellipse = new List<Vector2>();

            for (int i = 0; i < 90; i++)
            {
                float a = i / 90f * DiscGeometry.TwoPi;
                ellipse.Add(new Vector2(Mathf.Cos(a) * 0.86f, Mathf.Sin(a) * 0.90f));
            }

            PolarContour contour = PolarContour.FromDiscSpaceMm(ellipse);

            Assert.That(contour.VerticalDiameterMm, Is.EqualTo(1.80f).Within(0.02f));
            Assert.That(contour.HorizontalDiameterMm, Is.EqualTo(1.72f).Within(0.02f));
        }

        /// <summary>
        /// The end the clinician actually cares about: margins drawn by hand on an
        /// image whose auto-trace found no cup must still fit, and the CDR that
        /// comes back must be measured off the rebuilt surface.
        /// </summary>
        [Test]
        public void HandTracedMargins_FitAndMeasureLikeAnyOtherEye()
        {
            List<Vector2> disc = new List<Vector2>();
            List<Vector2> cup = new List<Vector2>();

            for (int i = 0; i < 72; i++)
            {
                float a = i / 72f * DiscGeometry.TwoPi;
                disc.Add(new Vector2(Mathf.Cos(a) * 0.90f, Mathf.Sin(a) * 0.90f));
                cup.Add(new Vector2(Mathf.Cos(a) * 0.54f, Mathf.Sin(a) * 0.54f));   // CDR 0.6
            }

            OnhFitInputs inputs = new OnhFitInputs();
            inputs.Disc = PolarContour.FromDiscSpaceMm(disc);
            inputs.Cup = PolarContour.FromDiscSpaceMm(cup);
            inputs.DepthProvenance = DepthProvenance.PopulationAssumed;

            OnhFitReport report = OnhSpecFitter.Fit(inputs);

            Assert.That(report.CupResidualRmsMm, Is.LessThan(OnhFitReport.AcceptableResidualMm));

            OnhMetrics measured = OnhMeasurementEngine.Measure(
                new OnhSurface(report.Spec), MeasurementMethod.SlopeContour);

            Assert.That(measured.VerticalCdr, Is.EqualTo(0.60f).Within(0.03f),
                "A hand-traced 0.6 cup did not measure back as 0.6.");
        }
    }
}
