// -----------------------------------------------------------------------------
// Ocular -- a traced outline, resampled into the disc-space polar form the rest
// of the project already speaks.
//
// A disc margin drawn on a photograph arrives as an arbitrary polygon in pixels.
// Everything downstream -- OnhSurface, the measurement engine, the mesh -- is
// written in terms of "radius in mm at angle theta, disc-space". This class is
// the single crossing point between those two worlds, so it is also the single
// place the two orientation traps can bite.
//
// TRAP 1, x mirroring. Disc-space puts TEMPORAL at theta = 0. The renderer takes
// laterality into account by mirroring x for OS (Laterality.MirrorsX), so import
// must mirror on exactly the same condition. If it does not, a photograph
// imported and re-rendered still looks like the photograph -- but nasal and
// temporal have swapped, so every ISNT judgement is reversed. Superior and
// inferior are untouched, which is the tell.
//
// TRAP 2, y flipping. Image rows run downward, disc-space y runs up, so the sign
// is flipped here. Get it wrong and SUPERIOR and INFERIOR swap instead, which
// leaves ISNT looking sane and inverts the visual-field correlation. See the
// header of FundusImage.
//
// Both conversions live in ToDiscSpace() and nowhere else.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using UnityEngine;
using Ocular.Core.Anatomy;

namespace Ocular.Core.Imaging
{
    public sealed class PolarContour
    {
        /// <summary>Angular bins. 360 puts one sample per degree, finer than any hand trace.</summary>
        public const int DefaultSamples = 360;

        private readonly float[] _radiusMm;

        public int SampleCount { get { return _radiusMm.Length; } }

        /// <summary>Centre this contour is measured about, in image pixels.</summary>
        public Vector2 CentrePixels { get; private set; }

        public PolarContour(float[] radiusMm, Vector2 centrePixels)
        {
            if (radiusMm == null || radiusMm.Length < 8)
                throw new ArgumentException("A polar contour needs at least 8 angular samples.");

            _radiusMm = radiusMm;
            CentrePixels = centrePixels;
        }

        /// <summary>Radius at a bin index, wrapping.</summary>
        public float RadiusAtBin(int bin)
        {
            int n = _radiusMm.Length;
            return _radiusMm[((bin % n) + n) % n];
        }

        /// <summary>Radius at an arbitrary disc-space angle, mm. Linear between bins, wraps.</summary>
        public float RadiusAt(float theta)
        {
            int n = _radiusMm.Length;
            float t = Mathf.Repeat(theta, DiscGeometry.TwoPi) / DiscGeometry.TwoPi * n;

            int i0 = Mathf.FloorToInt(t);
            float f = t - i0;

            return Mathf.Lerp(RadiusAtBin(i0), RadiusAtBin(i0 + 1), f);
        }

        /// <summary>
        /// Diameter across the disc along the axis through theta, mm -- the sum of
        /// the two opposing radii, not twice one of them. A tilted or oval disc has
        /// an off-centre geometric centre, and doubling a single radius reports it
        /// as much rounder than it is.
        /// </summary>
        public float DiameterAlong(float theta)
        {
            return RadiusAt(theta) + RadiusAt(theta + Mathf.PI);
        }

        public float VerticalDiameterMm { get { return DiameterAlong(DiscGeometry.HalfPi); } }
        public float HorizontalDiameterMm { get { return DiameterAlong(0f); } }

        /// <summary>
        /// Circular moving average over <paramref name="window"/> bins.
        ///
        /// <para>A hand trace is jittery at the pixel level and an auto-trace is
        /// jittery wherever a vessel crosses the margin. Left alone, that jitter
        /// becomes high-frequency rim-width noise, and the notch detector -- which
        /// is looking for the single deepest angular dip -- will happily lock onto
        /// a two-degree spike of tracing noise and report a focal notch.</para>
        /// </summary>
        public PolarContour Smoothed(int window)
        {
            int n = _radiusMm.Length;
            window = Mathf.Clamp(window, 1, n / 4);
            if (window <= 1) return this;

            float[] output = new float[n];
            int half = window / 2;

            for (int i = 0; i < n; i++)
            {
                float sum = 0f;
                for (int k = -half; k <= half; k++) sum += RadiusAtBin(i + k);
                output[i] = sum / (2 * half + 1);
            }

            return new PolarContour(output, CentrePixels);
        }

        /// <summary>Area enclosed, mm^2, by the polar shoelace sum. Used for disc and cup area.</summary>
        public float AreaMm2()
        {
            int n = _radiusMm.Length;
            float step = DiscGeometry.TwoPi / n;
            float sum = 0f;

            for (int i = 0; i < n; i++) sum += _radiusMm[i] * _radiusMm[i];

            return 0.5f * step * sum;
        }

        /// <summary>
        /// Converts one image pixel into disc-space millimetres about a centre.
        /// The ONLY place the mirror and the y flip are applied.
        /// </summary>
        public static Vector2 ToDiscSpace(Vector2 pixel, Vector2 centrePixels,
                                          ImageCalibration calibration, Laterality laterality)
        {
            float dx = pixel.x - centrePixels.x;
            float dy = -(pixel.y - centrePixels.y);   // image y is down, disc-space y is up

            if (laterality.MirrorsX()) dx = -dx;

            return new Vector2(calibration.PixelsToMm(dx), calibration.PixelsToMm(dy));
        }

        /// <summary>
        /// The exact inverse of <see cref="ToDiscSpace"/>: a disc-space polar
        /// coordinate back to an image pixel. Kept adjacent to the forward
        /// conversion so the mirror and the y flip can never be updated in one
        /// direction and forgotten in the other.
        /// </summary>
        public static Vector2 ToPixels(float theta, float radiusMm, Vector2 centrePixels,
                                       ImageCalibration calibration, Laterality laterality)
        {
            float x = radiusMm * Mathf.Cos(theta);
            float y = radiusMm * Mathf.Sin(theta);

            float dx = calibration.MmToPixels(laterality.MirrorsX() ? -x : x);
            float dy = -calibration.MmToPixels(y);

            return new Vector2(centrePixels.x + dx, centrePixels.y + dy);
        }

        /// <summary>Every bin of this contour as an image-space polygon.</summary>
        public Vector2[] ToPolygonPixels(ImageCalibration calibration, Laterality laterality)
        {
            int n = SampleCount;
            Vector2[] polygon = new Vector2[n];

            for (int i = 0; i < n; i++)
            {
                float theta = (i + 0.5f) / n * DiscGeometry.TwoPi;
                polygon[i] = ToPixels(theta, _radiusMm[i], CentrePixels, calibration, laterality);
            }

            return polygon;
        }

        /// <summary>
        /// Resamples a traced polygon into uniform angular bins.
        ///
        /// <para>Interpolates between the polygon's own vertices in angle rather
        /// than ray-casting against its edges. A disc or cup margin is star-shaped
        /// about its centre, so the two agree; but ray-casting against a hand trace
        /// that doubles back on itself by a pixel finds two intersections and has
        /// to guess, whereas angular interpolation degrades to a slightly smoothed
        /// outline and never produces a spike.</para>
        /// </summary>
        public static PolarContour FromPolygonPixels(IList<Vector2> polygonPixels,
                                                     Vector2 centrePixels,
                                                     ImageCalibration calibration,
                                                     Laterality laterality,
                                                     int samples = DefaultSamples)
        {
            if (polygonPixels == null || polygonPixels.Count < 3)
                throw new ArgumentException("A traced contour needs at least 3 points.");

            samples = Mathf.Clamp(samples, 16, 2048);

            int m = polygonPixels.Count;
            float[] theta = new float[m];
            float[] radius = new float[m];

            for (int i = 0; i < m; i++)
            {
                Vector2 d = ToDiscSpace(polygonPixels[i], centrePixels, calibration, laterality);

                theta[i] = Mathf.Repeat(Mathf.Atan2(d.y, d.x), DiscGeometry.TwoPi);
                radius[i] = d.magnitude;
            }

            Array.Sort(theta, radius);

            float[] output = new float[samples];

            for (int i = 0; i < samples; i++)
            {
                float t = (i + 0.5f) / samples * DiscGeometry.TwoPi;
                output[i] = InterpolateByAngle(theta, radius, t);
            }

            return new PolarContour(output, centrePixels);
        }

        /// <summary>
        /// Resamples a polygon already expressed in DISC-SPACE MILLIMETRES about
        /// the disc centre -- the form a hand trace taken in the 3D view arrives
        /// in, since the picking ray is intersected with the retinal plane and
        /// comes back in millimetres, not pixels.
        ///
        /// <para>No calibration and no laterality: both were already applied when
        /// the point was projected. Running them again here would mirror a left
        /// eye twice and rescale an already-scaled coordinate.</para>
        /// </summary>
        public static PolarContour FromDiscSpaceMm(IList<Vector2> pointsMm, int samples = DefaultSamples)
        {
            if (pointsMm == null || pointsMm.Count < 3)
                throw new ArgumentException("A traced contour needs at least 3 points.");

            samples = Mathf.Clamp(samples, 16, 2048);

            int m = pointsMm.Count;
            float[] theta = new float[m];
            float[] radius = new float[m];

            for (int i = 0; i < m; i++)
            {
                theta[i] = Mathf.Repeat(Mathf.Atan2(pointsMm[i].y, pointsMm[i].x), DiscGeometry.TwoPi);
                radius[i] = pointsMm[i].magnitude;
            }

            Array.Sort(theta, radius);

            float[] output = new float[samples];

            for (int i = 0; i < samples; i++)
            {
                float t = (i + 0.5f) / samples * DiscGeometry.TwoPi;
                output[i] = InterpolateByAngle(theta, radius, t);
            }

            return new PolarContour(output, Vector2.zero);
        }

        /// <summary>Builds a contour directly from per-bin radii, e.g. from a radial auto-trace.</summary>
        public static PolarContour FromRadii(float[] radiusMm, Vector2 centrePixels)
        {
            return new PolarContour((float[])radiusMm.Clone(), centrePixels);
        }

        private static float InterpolateByAngle(float[] theta, float[] radius, float t)
        {
            int m = theta.Length;

            // First vertex at or past t. Everything wraps, so "past the end" means
            // the arc that closes the polygon between the last vertex and the first.
            int hi = 0;
            while (hi < m && theta[hi] < t) hi++;

            int lo = (hi - 1 + m) % m;
            hi %= m;

            float a = theta[lo];
            float b = theta[hi];

            float span = Mathf.Repeat(b - a, DiscGeometry.TwoPi);
            if (span < 1e-6f) return radius[hi];

            float f = Mathf.Repeat(t - a, DiscGeometry.TwoPi) / span;

            return Mathf.Lerp(radius[lo], radius[hi], Mathf.Clamp01(f));
        }
    }
}
