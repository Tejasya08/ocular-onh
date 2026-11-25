// -----------------------------------------------------------------------------
// Ocular -- the gate on the image-derived path.
//
// The claim being defended here is that fitting a photograph does not break the
// round trip. A spec fitted from traced margins must generate a surface that the
// measurement engine reads the same way it reads an authored one, so the tests
// are written end to end: known surface -> synthesised trace -> fit -> rebuilt
// surface -> MEASURED metrics, compared against the original surface's measured
// metrics. Never against the numbers the fitter itself computed.
//
// Two of these tests exist purely to catch silent orientation errors, because
// those are the failures that look completely normal on screen: a y flip swaps
// superior and inferior, a wrong mirror swaps nasal and temporal, and in both
// cases you get a plausible disc with its damage in the wrong place.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Ocular.Core.Anatomy;
using Ocular.Core.Imaging;
using Ocular.Core.Measurement;

namespace Ocular.Tests
{
    public class FundusImportTests
    {
        private const int Samples = PolarContour.DefaultSamples;

        /// <summary>An asymmetric, notched disc -- the case a naive fit gets wrong.</summary>
        private static OpticNerveHeadSpec NotchedSpec()
        {
            OpticNerveHeadSpec s = OpticNerveHeadSpec.Healthy();

            s.DiscDiameterVerticalMm = 1.85f;
            s.DiscDiameterHorizontalMm = 1.70f;
            s.CupToDiscVertical = 0.62f;
            s.CupToDiscHorizontal = 0.55f;

            s.RimScaleInferior = 0.70f;
            s.RimScaleSuperior = 1.05f;
            s.RimScaleNasal = 1.15f;
            s.RimScaleTemporal = 0.85f;

            s.NotchAngleDeg = 270f;      // inferior
            s.NotchDepth = 0.55f;
            s.NotchWidthDeg = 14f;

            return s;
        }

        /// <summary>Traces the disc and cup margins straight off a surface, in mm.</summary>
        private static void TraceFromSurface(OnhSurface surface, out PolarContour disc, out PolarContour cup)
        {
            float[] discR = new float[Samples];
            float[] cupR = new float[Samples];

            for (int i = 0; i < Samples; i++)
            {
                float theta = (i + 0.5f) / Samples * DiscGeometry.TwoPi;
                discR[i] = surface.DiscRadiusMm(theta);
                cupR[i] = surface.CupRadiusMm(theta);
            }

            disc = PolarContour.FromRadii(discR, Vector2.zero);
            cup = PolarContour.FromRadii(cupR, Vector2.zero);
        }

        private static OnhFitInputs InputsFrom(OnhSurface surface)
        {
            PolarContour disc, cup;
            TraceFromSurface(surface, out disc, out cup);

            OnhFitInputs inputs = new OnhFitInputs();
            inputs.Disc = disc;
            inputs.Cup = cup;
            inputs.DepthProvenance = DepthProvenance.OctMeasured;
            inputs.CupDepthMm = surface.Spec.CupDepthMm;

            return inputs;
        }

        // =====================================================================
        // The round trip
        // =====================================================================

        [Test]
        public void Fit_RebuiltSurface_FollowsTheTracedMargins()
        {
            OnhSurface original = new OnhSurface(NotchedSpec());
            OnhFitReport report = OnhSpecFitter.Fit(InputsFrom(original));

            Assert.That(report.DiscResidualRmsMm, Is.LessThan(OnhFitReport.AcceptableResidualMm),
                "The rebuilt disc margin drifted from the trace.");

            Assert.That(report.CupResidualRmsMm, Is.LessThan(OnhFitReport.AcceptableResidualMm),
                "The rebuilt cup margin drifted from the trace. The fit is meant to be cup-exact.");
        }

        /// <summary>
        /// The one that matters. The CDR reported for an imported eye must come out
        /// of the rebuilt geometry, and must agree with what the same engine reads
        /// off the eye the trace was taken from.
        ///
        /// <para>If the fitter ever started stashing its own computed ratio and the
        /// UI started reading that, this test would fail while everything on screen
        /// still looked right -- which is the entire reason it is written against
        /// measured values on both sides rather than against the spec.</para>
        /// </summary>
        [Test]
        public void Fit_MeasuredCdr_MatchesTheOriginalEyesMeasuredCdr(
            [Values(MeasurementMethod.ReferencePlane, MeasurementMethod.SlopeContour)] MeasurementMethod method)
        {
            OnhSurface original = new OnhSurface(NotchedSpec());
            OnhFitReport report = OnhSpecFitter.Fit(InputsFrom(original));
            OnhSurface rebuilt = new OnhSurface(report.Spec);

            OnhMetrics before = OnhMeasurementEngine.Measure(original, method);
            OnhMetrics after = OnhMeasurementEngine.Measure(rebuilt, method);

            Assert.That(after.VerticalCdr, Is.EqualTo(before.VerticalCdr).Within(0.02f));
            Assert.That(after.HorizontalCdr, Is.EqualTo(before.HorizontalCdr).Within(0.02f));
            Assert.That(after.RimAreaMm2, Is.EqualTo(before.RimAreaMm2).Within(0.05f));
        }

        [Test]
        public void Fit_RecoversTheNotch_InTheSectorItWasAuthoredIn()
        {
            OnhSurface original = new OnhSurface(NotchedSpec());
            OnhFitReport report = OnhSpecFitter.Fit(InputsFrom(original));

            Assert.That(report.NotchDetected, Is.True, "A 55% inferior notch was not detected.");

            float delta = DiscGeometry.AngleDelta(report.Spec.NotchAngleDeg * Mathf.Deg2Rad,
                                                  270f * Mathf.Deg2Rad) * Mathf.Rad2Deg;

            Assert.That(delta, Is.LessThan(20f),
                "The notch was recovered in the wrong place: " + report.Spec.NotchAngleDeg + " deg.");
        }

        [Test]
        public void Fit_HealthyDisc_ReportsNoNotch()
        {
            OnhSurface healthy = new OnhSurface(OpticNerveHeadSpec.Healthy());
            OnhFitReport report = OnhSpecFitter.Fit(InputsFrom(healthy));

            Assert.That(report.NotchDetected, Is.False,
                "A normal disc was given a focal notch. The detection threshold is too low, "
                + "and every healthy eye will be over-called.");
        }

        // =====================================================================
        // Provenance -- the numbers you are not entitled to
        // =====================================================================

        [Test]
        public void CircularCalibration_MarksDiscSizeAsNotMeasured()
        {
            OnhSurface surface = new OnhSurface(NotchedSpec());

            OnhFitInputs inputs = InputsFrom(surface);
            inputs.CalibrationIsCircular = true;

            OnhFitReport report = OnhSpecFitter.Fit(inputs);

            Assert.That(report.DiscDiameterIsMeasured, Is.False,
                "Scale taken from an assumed disc diameter still claimed to have measured one.");

            Assert.That(report.Caveats.Count, Is.GreaterThan(0),
                "A circular calibration produced no caveat for the reader.");
        }

        [Test]
        public void AssumedDiscDiameter_IsFlaggedByTheCalibrationItself()
        {
            Assert.That(ImageCalibration.FromAssumedDiscDiameter(300f, 1.8f).IsCircular, Is.True);
            Assert.That(ImageCalibration.FromScaleBar(100f, 1f).IsCircular, Is.False);
            Assert.That(ImageCalibration.FromCameraField(30f, 1024f, 24f).IsCircular, Is.False);
        }

        [Test]
        public void NoOctDepth_IsAssumed_AndSaysSo()
        {
            OnhSurface surface = new OnhSurface(NotchedSpec());

            OnhFitInputs inputs = InputsFrom(surface);
            inputs.DepthProvenance = DepthProvenance.PopulationAssumed;

            OnhFitReport report = OnhSpecFitter.Fit(inputs);

            Assert.That(report.DepthProvenance, Is.EqualTo(DepthProvenance.PopulationAssumed));
            Assert.That(report.DepthLabel(), Is.EqualTo("ASSUMED"));
            Assert.That(report.Caveats.Count, Is.GreaterThan(0));
        }

        [Test]
        public void OctDepth_IsCarriedThroughUnchanged()
        {
            OnhSurface surface = new OnhSurface(NotchedSpec());

            OnhFitInputs inputs = InputsFrom(surface);
            inputs.DepthProvenance = DepthProvenance.OctMeasured;
            inputs.CupDepthMm = 0.71f;

            OnhFitReport report = OnhSpecFitter.Fit(inputs);

            Assert.That(report.Spec.CupDepthMm, Is.EqualTo(0.71f).Within(1e-4f));
            Assert.That(report.DepthLabel(), Is.EqualTo("OCT"));
        }

        /// <summary>
        /// A myopic eye is longer, so at the same camera field its retinal image is
        /// larger and its disc covers more pixels. Fewer pixels per millimetre.
        /// Getting this backwards makes long eyes report small discs, and small
        /// discs are read as more damaged at any given CDR.
        /// </summary>
        [Test]
        public void LongerEye_GivesFewerPixelsPerMillimetre()
        {
            ImageCalibration shortEye = ImageCalibration.FromCameraField(30f, 1024f, 22.0f);
            ImageCalibration longEye = ImageCalibration.FromCameraField(30f, 1024f, 26.0f);

            Assert.That(longEye.PixelsPerMm, Is.LessThan(shortEye.PixelsPerMm));
        }

        // =====================================================================
        // Orientation -- the failures that look normal
        // =====================================================================

        /// <summary>
        /// Image y runs DOWN, disc-space y runs UP. A margin point below the disc
        /// centre in the picture is INFERIOR, at 270 degrees.
        ///
        /// <para>Without this test a y flip is undetectable by eye: the disc still
        /// looks like a disc, but superior and inferior have swapped, so an
        /// inferior notch is modelled superiorly and -- because the optical
        /// inversion then flips it again -- the simulated field defect comes out on
        /// the side it started on. Two wrongs producing a plausible answer.</para>
        /// </summary>
        [Test]
        public void PixelBelowTheCentre_IsInferior()
        {
            ImageCalibration cal = ImageCalibration.FromScaleBar(100f, 1f);
            Vector2 centre = new Vector2(500f, 400f);

            Vector2 disc = PolarContour.ToDiscSpace(new Vector2(500f, 500f), centre, cal, Laterality.OD);

            float theta = Mathf.Repeat(Mathf.Atan2(disc.y, disc.x), DiscGeometry.TwoPi);

            Assert.That(DiscGeometry.SectorAt(theta), Is.EqualTo(DiscSector.Inferior),
                "A point lower in the image was not read as inferior -- the y flip is wrong.");
        }

        /// <summary>
        /// The mirror must match Laterality.MirrorsX, which the renderer already
        /// uses. A point to the right of the disc centre is temporal in an OD image
        /// and nasal in an OS one; if import and render ever disagreed, ISNT would
        /// invert for one eye only.
        /// </summary>
        [Test]
        public void PixelRightOfTheCentre_SwapsNasalAndTemporalBetweenEyes()
        {
            ImageCalibration cal = ImageCalibration.FromScaleBar(100f, 1f);
            Vector2 centre = new Vector2(500f, 400f);
            Vector2 pixel = new Vector2(600f, 400f);

            Vector2 od = PolarContour.ToDiscSpace(pixel, centre, cal, Laterality.OD);
            Vector2 os = PolarContour.ToDiscSpace(pixel, centre, cal, Laterality.OS);

            Assert.That(DiscGeometry.SectorAt(Mathf.Atan2(od.y, od.x)), Is.EqualTo(DiscSector.Temporal));
            Assert.That(DiscGeometry.SectorAt(Mathf.Atan2(os.y, os.x)), Is.EqualTo(DiscSector.Nasal));
        }

        [Test]
        public void PixelConversion_RoundTrips()
        {
            ImageCalibration cal = ImageCalibration.FromCameraField(30f, 1024f, 24f);
            Vector2 centre = new Vector2(512f, 480f);

            const float theta = 1.1f;
            const float radiusMm = 0.83f;

            Vector2 pixel = PolarContour.ToPixels(theta, radiusMm, centre, cal, Laterality.OS);
            Vector2 back = PolarContour.ToDiscSpace(pixel, centre, cal, Laterality.OS);

            Assert.That(back.magnitude, Is.EqualTo(radiusMm).Within(1e-3f));
            Assert.That(Mathf.Repeat(Mathf.Atan2(back.y, back.x), DiscGeometry.TwoPi),
                        Is.EqualTo(theta).Within(1e-3f));
        }

        // =====================================================================
        // Tracing
        // =====================================================================

        [Test]
        public void PolygonTrace_RecoversTheDiameterItWasDrawnWith()
        {
            ImageCalibration cal = ImageCalibration.FromScaleBar(200f, 1f);   // 200 px per mm
            Vector2 centre = new Vector2(400f, 400f);

            // An ellipse 1.8 mm tall and 1.6 mm wide.
            List<Vector2> polygon = new List<Vector2>();

            for (int i = 0; i < 120; i++)
            {
                float a = i / 120f * DiscGeometry.TwoPi;
                polygon.Add(new Vector2(centre.x + Mathf.Cos(a) * 0.8f * 200f,
                                        centre.y + Mathf.Sin(a) * 0.9f * 200f));
            }

            PolarContour contour = PolarContour.FromPolygonPixels(polygon, centre, cal, Laterality.OD);

            Assert.That(contour.VerticalDiameterMm, Is.EqualTo(1.8f).Within(0.02f));
            Assert.That(contour.HorizontalDiameterMm, Is.EqualTo(1.6f).Within(0.02f));
        }

        /// <summary>
        /// A retinal vessel crossing the disc margin cuts a handful of adjacent rays
        /// short. The median filter has to discard them; a mean would pull the
        /// margin inward and manufacture a notch exactly where the vessels are.
        /// </summary>
        [Test]
        public void CircularMedian_RejectsAVesselShapedDropout()
        {
            float[] radii = new float[64];
            for (int i = 0; i < radii.Length; i++) radii[i] = 100f;

            radii[20] = 40f;
            radii[21] = 38f;
            radii[22] = 42f;

            float[] filtered = DiscSegmenter.CircularMedian(radii, 9);

            for (int i = 0; i < filtered.Length; i++)
                Assert.That(filtered[i], Is.EqualTo(100f).Within(1e-3f),
                    "A three-ray dropout survived a 9-wide median at index " + i + ".");
        }

        [Test]
        public void Segmenter_FindsASyntheticDisc()
        {
            const int Size = 400;
            const float DiscRadiusPx = 90f;
            const float CupRadiusPx = 45f;

            FundusImage image = new FundusImage(Size, Size);
            Vector2 centre = new Vector2(Size * 0.5f, Size * 0.5f);

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x, y), centre);

                    // Retina, rim, cup: three flat bands, each brighter than the last.
                    if (d > DiscRadiusPx) image.SetPixel(x, y, 0.45f, 0.20f, 0.12f);
                    else if (d > CupRadiusPx) image.SetPixel(x, y, 0.80f, 0.62f, 0.45f);
                    else image.SetPixel(x, y, 0.95f, 0.92f, 0.85f);
                }
            }

            ImageCalibration cal = ImageCalibration.FromScaleBar(100f, 1f);

            SegmentationResult result = DiscSegmenter.Segment(image, centre, 150f, cal, Laterality.OD);

            float expectedDiscMm = cal.PixelsToMm(DiscRadiusPx * 2f);
            float expectedCupMm = cal.PixelsToMm(CupRadiusPx * 2f);

            Assert.That(result.Disc.VerticalDiameterMm, Is.EqualTo(expectedDiscMm).Within(0.08f));
            Assert.That(result.Cup.VerticalDiameterMm, Is.EqualTo(expectedCupMm).Within(0.10f));
            Assert.That(result.CupIsReliable, Is.True);
        }

        /// <summary>
        /// A disc with no pallor gradient gives the cup tracer nothing to work with.
        /// It must say so rather than return a confident circle: pallor and cupping
        /// are different findings, and a cup traced from colour on a disc that has
        /// no colour signal is an invention.
        /// </summary>
        [Test]
        public void Segmenter_FlagsAnUnreliableCup_WhenThereIsNoPallor()
        {
            const int Size = 400;
            const float DiscRadiusPx = 90f;

            FundusImage image = new FundusImage(Size, Size);
            Vector2 centre = new Vector2(Size * 0.5f, Size * 0.5f);

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x, y), centre);

                    if (d > DiscRadiusPx) image.SetPixel(x, y, 0.45f, 0.20f, 0.12f);
                    else image.SetPixel(x, y, 0.80f, 0.62f, 0.45f);   // uniform disc, no cup
                }
            }

            ImageCalibration cal = ImageCalibration.FromScaleBar(100f, 1f);

            SegmentationResult result = DiscSegmenter.Segment(image, centre, 150f, cal, Laterality.OD);

            Assert.That(result.CupIsReliable, Is.False);
            Assert.That(result.Note, Is.Not.Empty);
        }
    }
}
