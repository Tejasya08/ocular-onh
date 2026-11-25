// -----------------------------------------------------------------------------
// Ocular -- a first-pass automatic trace of the disc and cup margins.
//
// This is a STARTING POINT FOR A CLINICIAN TO CORRECT, and the UI says so. It is
// classical image processing -- adaptive radial thresholding with a circular
// median filter -- not a segmentation network, and it is deterministic, which is
// what lets it be asserted in an EditMode test.
//
// The two margins are not equally honest:
//
//   DISC MARGIN. Genuinely findable. The disc is brighter than the surrounding
//   retina in red and green, and the transition at the margin is sharp. The
//   auto-trace is usually within a pixel or two of a hand trace.
//
//   CUP MARGIN. Fundamentally approximate, and the reason a clinician must
//   review it. The cup margin is not a colour boundary -- it is defined by where
//   the surface bends away, which an ophthalmoscopist reads off the BEND OF THE
//   VESSELS as they dive over the edge. Pallor and cupping are different things
//   and they routinely disagree: a pale but shallow disc over-segments, an
//   excavated but well-perfused one under-segments. Tracing pallor and calling
//   it the cup is the classic error this file is capable of making, so it
//   reports its own contrast and flags the result unreliable when the pallor
//   signal is weak.
//
// Rays are walked in DISC-SPACE angles and converted to pixel offsets per step,
// so the radii come out already aligned with the bins PolarContour uses. Doing
// it in image angles and rotating afterwards is where an off-by-one-bin rotation
// creeps in, and a rotated rim profile puts the notch in the wrong sector.
// -----------------------------------------------------------------------------

using System;
using UnityEngine;
using Ocular.Core.Anatomy;

namespace Ocular.Core.Imaging
{
    public sealed class SegmentationResult
    {
        public PolarContour Disc;
        public PolarContour Cup;
        public Vector2 CentrePixels;

        /// <summary>False when disc pallor was too weak to place a cup margin from colour alone.</summary>
        public bool CupIsReliable = true;

        /// <summary>Pallor contrast actually found, 0..1. Below ~0.06 the cup trace is guesswork.</summary>
        public float CupContrast;

        /// <summary>
        /// Fraction of radial rays that never found pallor at all, 0..1.
        ///
        /// <para>The honest confidence signal. Those rays did not measure a
        /// narrow cup -- they measured nothing, and whatever radius they report
        /// came from a neighbour via the median filter. A contour assembled
        /// mostly from neighbours is an interpolation wearing a measurement's
        /// clothes.</para>
        /// </summary>
        public float CollapsedRayFraction;

        public string Note = string.Empty;
    }

    public static class DiscSegmenter
    {
        /// <summary>Consecutive sub-threshold samples needed to accept a margin crossing.</summary>
        private const int RunLength = 3;

        /// <summary>Radial step, pixels. Half a pixel keeps the margin sub-pixel without aliasing.</summary>
        private const float StepPixels = 0.5f;

        /// <summary>Below this pallor contrast the cup trace is flagged unreliable.</summary>
        public const float MinCupContrast = 0.06f;

        /// <summary>
        /// Above this fraction of failed rays the cup contour is not a measurement.
        ///
        /// <para>Set deliberately low. Failed rays arrive in CONTIGUOUS ARCS --
        /// typically wherever the vessel trunk crosses the margin -- so the
        /// circular median cannot repair them, it can only smear a neighbour's
        /// answer across the gap. One ray in ten failing already means a tenth of
        /// the contour is invented, and on a colour photograph the arc that fails
        /// is often the one carrying the clinically interesting notch.</para>
        /// </summary>
        public const float MaxCollapsedRayFraction = 0.10f;

        /// <summary>Centre movement, in pixels, below which re-centring has converged.</summary>
        private const float RecentreTolerancePixels = 0.5f;

        /// <summary>Backstop on the re-centring loop if the centre will not settle.</summary>
        private const int MaxRecentrePasses = 8;

        /// <summary>
        /// Locates the disc by finding the brightest region of a heavily blurred
        /// image. The blur radius should be comparable to a disc radius: blur less
        /// and a specular reflection wins, blur more and the whole posterior pole
        /// smears into one bright smudge whose peak is nowhere in particular.
        /// </summary>
        public static Vector2 FindDiscSeed(FundusImage image, int blurRadiusPixels)
        {
            FundusImage blurred = image.BlurredDiscChannel(blurRadiusPixels);

            float best = float.MinValue;
            Vector2 seed = new Vector2(image.Width * 0.5f, image.Height * 0.5f);

            for (int y = 0; y < image.Height; y++)
            {
                for (int x = 0; x < image.Width; x++)
                {
                    float v = blurred.DiscChannel(x, y);
                    if (v > best)
                    {
                        best = v;
                        seed = new Vector2(x, y);
                    }
                }
            }

            return seed;
        }

        /// <summary>
        /// Full first-pass segmentation. <paramref name="searchRadiusPixels"/> bounds
        /// how far from the seed the disc margin may be; derive it from the
        /// calibration (a disc is never wider than about 3.2 mm) rather than
        /// guessing in pixels.
        /// </summary>
        public static SegmentationResult Segment(FundusImage image,
                                                 Vector2 seedPixels,
                                                 float searchRadiusPixels,
                                                 ImageCalibration calibration,
                                                 Laterality laterality,
                                                 int samples = PolarContour.DefaultSamples)
        {
            if (image == null) throw new ArgumentNullException("image");

            samples = Mathf.Clamp(samples, 32, 1024);
            searchRadiusPixels = Mathf.Max(searchRadiusPixels, 8f);

            SegmentationResult result = new SegmentationResult();

            Vector2 centre = seedPixels;
            float[] discPx = null;

            // Trace, re-centre on what was traced, repeat until the centre stops
            // moving. A seed sitting off-centre inside the disc biases every ray on
            // one side short and the opposite side long.
            //
            // Iterated to convergence rather than run a fixed couple of times,
            // because a fixed count leaves a residual offset that is invisible in
            // the headline numbers and corrupting underneath them: opposing radii
            // still sum correctly, so the DIAMETERS come out right and the disc
            // looks correctly measured, while every individual radius carries the
            // offset. That lands as a per-meridian rim-width error -- which is what
            // ISNT, DDLS and the notch detector all read.
            //
            // Converges in three or four passes from a blurred-argmax seed; the cap
            // only matters for a pathological image where the centre oscillates.
            for (int pass = 0; pass < MaxRecentrePasses; pass++)
            {
                discPx = TraceRadii(image, centre, searchRadiusPixels, samples, laterality,
                                    true, DiscThreshold(image, centre, searchRadiusPixels, laterality));

                discPx = CircularMedian(discPx, 9);

                Vector2 moved = RecentreFromRadii(centre, discPx, laterality);
                float shift = Vector2.Distance(moved, centre);
                centre = moved;

                if (shift < RecentreTolerancePixels) break;
            }

            float cupContrast;
            float cupCollapsed;
            float[] cupPx = TraceCup(image, centre, discPx, samples, laterality,
                                     out cupContrast, out cupCollapsed);

            result.CentrePixels = centre;
            result.CupContrast = cupContrast;
            result.CollapsedRayFraction = cupCollapsed;

            // Contrast alone was never enough. A disc can have ample pallor
            // contrast and still yield a useless contour when the vessel trunk
            // hides the margin over an arc -- so the fraction of rays that never
            // found pallor is the second, and more honest, gate.
            result.CupIsReliable = cupContrast >= MinCupContrast
                                && cupCollapsed <= MaxCollapsedRayFraction;

            if (!result.CupIsReliable)
            {
                result.Note = cupContrast < MinCupContrast
                    ? "Disc pallor was too weak to place the cup margin from colour (contrast "
                      + cupContrast.ToString("0.000") + "). The cup shown is a placeholder -- "
                      + "trace it by hand against the vessel bends."
                    : Mathf.RoundToInt(cupCollapsed * 100f) + "% of radial rays never reached "
                      + "pallor, usually where the central vessel trunk covers the margin. The "
                      + "contour across those arcs is interpolated from neighbours, not measured "
                      + "-- trace the cup by hand against the vessel bends.";
            }

            result.Disc = PolarContour.FromRadii(ToMm(discPx, calibration), centre);
            result.Cup = PolarContour.FromRadii(ToMm(cupPx, calibration), centre);

            return result;
        }

        private static float[] ToMm(float[] pixels, ImageCalibration calibration)
        {
            float[] mm = new float[pixels.Length];
            for (int i = 0; i < pixels.Length; i++) mm[i] = calibration.PixelsToMm(pixels[i]);
            return mm;
        }

        /// <summary>Disc-space direction expressed as a pixel-space offset direction.</summary>
        private static Vector2 RayDirection(float theta, Laterality laterality)
        {
            float dx = Mathf.Cos(theta);
            float dy = -Mathf.Sin(theta);          // image y runs down

            if (laterality.MirrorsX()) dx = -dx;

            return new Vector2(dx, dy);
        }

        /// <summary>
        /// Half-max threshold between the mean brightness inside the disc and the
        /// mean of the surrounding retina. Adaptive rather than absolute because
        /// fundus exposure varies by more between two cameras than the disc differs
        /// from the retina within one image.
        /// </summary>
        private static float DiscThreshold(FundusImage image, Vector2 centre, float searchRadius,
                                           Laterality laterality)
        {
            float interior = MeanOverAnnulus(image, centre, 0f, 0.3f * searchRadius, laterality, true);
            float background = MeanOverAnnulus(image, centre, 0.85f * searchRadius, searchRadius, laterality, true);

            return background + 0.5f * (interior - background);
        }

        private static float MeanOverAnnulus(FundusImage image, Vector2 centre,
                                             float innerPx, float outerPx,
                                             Laterality laterality, bool discChannel)
        {
            const int Rays = 72;

            double sum = 0.0;
            int count = 0;

            for (int i = 0; i < Rays; i++)
            {
                float theta = (i + 0.5f) / Rays * DiscGeometry.TwoPi;
                Vector2 dir = RayDirection(theta, laterality);

                for (float r = innerPx; r <= outerPx; r += 1f)
                {
                    float x = centre.x + dir.x * r;
                    float y = centre.y + dir.y * r;
                    if (!image.Contains(x, y)) continue;

                    sum += discChannel ? image.SampleDiscChannel(x, y) : image.SampleGreenChannel(x, y);
                    count++;
                }
            }

            return count > 0 ? (float)(sum / count) : 0f;
        }

        /// <summary>
        /// Walks each meridian outward and returns the radius at which brightness
        /// stays below the threshold for <see cref="RunLength"/> consecutive steps.
        ///
        /// <para>The run requirement is the whole reason this survives contact with
        /// a real photograph. A retinal vessel crossing the margin is a dark line
        /// two or three pixels wide; a single-sample test stops there, on every ray
        /// a vessel happens to cross, and the circular median filter afterwards
        /// cannot rescue it because major vessels emerge in a bundle and take
        /// several adjacent rays down together. That failure looks exactly like a
        /// focal notch, in the superior and inferior sectors where the vessels are
        /// -- which is where real notches also occur.</para>
        /// </summary>
        private static float[] TraceRadii(FundusImage image, Vector2 centre, float maxRadius,
                                          int samples, Laterality laterality,
                                          bool discChannel, float threshold)
        {
            float[] radii = new float[samples];
            float startRadius = 0.15f * maxRadius;

            for (int i = 0; i < samples; i++)
            {
                float theta = (i + 0.5f) / samples * DiscGeometry.TwoPi;
                Vector2 dir = RayDirection(theta, laterality);

                float found = maxRadius;
                int run = 0;

                for (float r = startRadius; r <= maxRadius; r += StepPixels)
                {
                    float x = centre.x + dir.x * r;
                    float y = centre.y + dir.y * r;

                    // Off the edge of the picture is the end of the evidence, not a
                    // margin. Reporting maxRadius here keeps the median filter from
                    // treating a cropped disc as a real, very large one.
                    if (!image.Contains(x, y)) break;

                    float v = discChannel ? image.SampleDiscChannel(x, y) : image.SampleGreenChannel(x, y);

                    if (v < threshold)
                    {
                        run++;
                        if (run >= RunLength)
                        {
                            found = r - (RunLength - 1) * StepPixels;
                            break;
                        }
                    }
                    else
                    {
                        run = 0;
                    }
                }

                radii[i] = Mathf.Clamp(found, StepPixels, maxRadius);
            }

            return radii;
        }

        private static float[] TraceCup(FundusImage image, Vector2 centre, float[] discRadiiPx,
                                        int samples, Laterality laterality, out float contrast,
                                        out float collapsedFraction)
        {
            float meanDisc = 0f;
            for (int i = 0; i < discRadiiPx.Length; i++) meanDisc += discRadiiPx[i];
            meanDisc /= discRadiiPx.Length;

            float rimMean = MeanOverAnnulus(image, centre, 0.6f * meanDisc, 0.9f * meanDisc, laterality, false);
            float cupPeak = PercentileOverDisc(image, centre, 0.35f * meanDisc, laterality, 0.9f);

            contrast = cupPeak - rimMean;

            float[] cup = new float[samples];

            collapsedFraction = 1f;

            if (contrast < MinCupContrast)
            {
                // A placeholder the clinician is told to replace, not a measurement.
                // Deliberately a plain circle at a modest CDR so it never passes for
                // a real trace: a wrong-looking cup gets corrected, a plausible one
                // gets accepted.
                for (int i = 0; i < samples; i++) cup[i] = 0.3f * discRadiiPx[i];
                return cup;
            }

            float threshold = rimMean + 0.55f * contrast;

            int collapsed = 0;

            for (int i = 0; i < samples; i++)
            {
                float theta = (i + 0.5f) / samples * DiscGeometry.TwoPi;
                Vector2 dir = RayDirection(theta, laterality);

                float limit = 0.95f * discRadiiPx[i];
                float found = limit;
                int run = 0;

                // THE GUARD THAT MATTERS. A ray may not declare a cup margin
                // until it has actually been inside the pallor at least once.
                //
                // The central retinal vessels emerge FROM the cup and fan out
                // from the disc centre, so a ray starting at r = 0 lands on the
                // trunk and goes dark immediately. Without this, every ray
                // terminates within a few pixels and the cup collapses to
                // nothing -- measured on a real photograph as CDR 0.004, which
                // then renders as a flat disc. Four independent segmentation
                // approaches were tried on that image and this guard, not any
                // amount of threshold tuning, is what separated the ones that
                // found a cup from the ones that did not.
                bool insidePallor = false;

                for (float r = StepPixels; r <= limit; r += StepPixels)
                {
                    float x = centre.x + dir.x * r;
                    float y = centre.y + dir.y * r;
                    if (!image.Contains(x, y)) break;

                    if (image.SampleGreenChannel(x, y) < threshold)
                    {
                        if (!insidePallor) continue;   // still on the vessel trunk

                        run++;
                        if (run >= RunLength)
                        {
                            found = r - (RunLength - 1) * StepPixels;
                            break;
                        }
                    }
                    else
                    {
                        insidePallor = true;
                        run = 0;
                    }
                }

                // A ray that never saw pallor has not measured a small cup; it
                // has failed. Counting these is the only honest confidence
                // signal this method has.
                if (!insidePallor) collapsed++;

                cup[i] = Mathf.Clamp(found, StepPixels, limit);
            }

            collapsedFraction = (float)collapsed / samples;

            return CircularMedian(cup, 9);
        }

        private static float PercentileOverDisc(FundusImage image, Vector2 centre, float radiusPx,
                                                Laterality laterality, float percentile)
        {
            const int Rays = 72;

            System.Collections.Generic.List<float> values = new System.Collections.Generic.List<float>();

            for (int i = 0; i < Rays; i++)
            {
                float theta = (i + 0.5f) / Rays * DiscGeometry.TwoPi;
                Vector2 dir = RayDirection(theta, laterality);

                for (float r = 0f; r <= radiusPx; r += 1f)
                {
                    float x = centre.x + dir.x * r;
                    float y = centre.y + dir.y * r;
                    if (!image.Contains(x, y)) continue;

                    values.Add(image.SampleGreenChannel(x, y));
                }
            }

            if (values.Count == 0) return 0f;

            values.Sort();
            int index = Mathf.Clamp(Mathf.RoundToInt(percentile * (values.Count - 1)), 0, values.Count - 1);

            return values[index];
        }

        /// <summary>
        /// Median rather than mean, because the artefacts here are one-sided: a
        /// vessel can only cut a ray SHORT, never long. Averaging pulls the whole
        /// margin inward in proportion to how many vessels crossed it; a median
        /// discards them outright as long as they are the minority of the window.
        /// </summary>
        public static float[] CircularMedian(float[] values, int window)
        {
            int n = values.Length;
            window = Mathf.Clamp(window | 1, 1, Mathf.Max(1, n / 4) | 1);
            if (window <= 1) return values;

            float[] output = new float[n];
            float[] scratch = new float[window];
            int half = window / 2;

            for (int i = 0; i < n; i++)
            {
                for (int k = -half; k <= half; k++)
                {
                    int index = ((i + k) % n + n) % n;
                    scratch[k + half] = values[index];
                }

                Array.Sort(scratch);
                output[i] = scratch[half];
            }

            return output;
        }

        /// <summary>
        /// Moves the centre to the centroid of the traced margin, in pixel space.
        /// The radii are indexed in disc-space, so the direction conversion has to
        /// be applied here too rather than assuming image angles.
        /// </summary>
        private static Vector2 RecentreFromRadii(Vector2 centre, float[] radiiPx, Laterality laterality)
        {
            int n = radiiPx.Length;
            Vector2 sum = Vector2.zero;

            for (int i = 0; i < n; i++)
            {
                float theta = (i + 0.5f) / n * DiscGeometry.TwoPi;
                Vector2 dir = RayDirection(theta, laterality);
                sum += centre + dir * radiiPx[i];
            }

            return sum / n;
        }
    }
}
