// -----------------------------------------------------------------------------
// Ocular -- a fundus photograph as plain numbers.
//
// Deliberately NOT a Texture2D. Everything in the import path -- calibration,
// segmentation, contour tracing, spec fitting -- is pure C# operating on this
// class, which means the whole pipeline is constructible and assertable in an
// EditMode test without a graphics device, and runs unchanged in a WebGL player
// if a clinician ever uploads their own photograph at runtime. The conversion
// from Texture2D happens once, in Ocular.Anatomy.FundusPhotoTexture.
//
// COORDINATE CONVENTION: x is right, y is DOWN. Row 0 is the TOP of the picture,
// which is how every image file on disk is laid out and the opposite of Unity's
// Texture2D, whose row 0 is the bottom.
//
// That flip is worth being loud about, because getting it wrong is invisible.
// A vertically flipped fundus photograph is still a perfectly plausible-looking
// fundus photograph -- but it swaps SUPERIOR and INFERIOR. The disc would then
// be fitted with its notch in the wrong hemisphere, and because an inferior
// notch produces a SUPERIOR field defect (see VisualFieldModel), the simulation
// would go on to teach the field correlation exactly backwards. Nothing would
// look wrong at any point.
//
// The other half of the same trap lives in PolarContour: mirroring x wrongly
// swaps NASAL and TEMPORAL, which breaks ISNT instead. Two distinct failures,
// each with its own tell.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace Ocular.Core.Imaging
{
    public sealed class FundusImage
    {
        private readonly float[] _r;
        private readonly float[] _g;
        private readonly float[] _b;

        public int Width { get; private set; }
        public int Height { get; private set; }

        public FundusImage(int width, int height)
        {
            Width = Mathf.Max(1, width);
            Height = Mathf.Max(1, height);

            int n = Width * Height;
            _r = new float[n];
            _g = new float[n];
            _b = new float[n];
        }

        public void SetPixel(int x, int y, float r, float g, float b)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height) return;

            int i = y * Width + x;
            _r[i] = r;
            _g[i] = g;
            _b[i] = b;
        }

        public void GetPixel(int x, int y, out float r, out float g, out float b)
        {
            x = Mathf.Clamp(x, 0, Width - 1);
            y = Mathf.Clamp(y, 0, Height - 1);

            int i = y * Width + x;
            r = _r[i];
            g = _g[i];
            b = _b[i];
        }

        public bool Contains(float x, float y)
        {
            return x >= 0f && y >= 0f && x <= Width - 1 && y <= Height - 1;
        }

        /// <summary>
        /// Brightness used to find the DISC MARGIN.
        ///
        /// <para>Weighted toward red and green because that is where the disc
        /// stands out from the surrounding retina. Blue carries almost no useful
        /// fundus signal -- it is mostly scatter and camera noise -- and including
        /// it at equal weight measurably softens the margin.</para>
        /// </summary>
        public float DiscChannel(int x, int y)
        {
            float r, g, b;
            GetPixel(x, y, out r, out g, out b);
            return 0.55f * r + 0.45f * g;
        }

        /// <summary>
        /// The red-free view. Vessels and the nerve fibre layer show best here, so
        /// this is the channel used when looking for pallor inside the disc.
        /// </summary>
        public float GreenChannel(int x, int y)
        {
            float r, g, b;
            GetPixel(x, y, out r, out g, out b);
            return g;
        }

        /// <summary>Bilinear sample of <see cref="DiscChannel"/>. Clamps at the border.</summary>
        public float SampleDiscChannel(float x, float y)
        {
            return SampleBilinear(x, y, true);
        }

        /// <summary>Bilinear sample of the green channel. Clamps at the border.</summary>
        public float SampleGreenChannel(float x, float y)
        {
            return SampleBilinear(x, y, false);
        }

        private float SampleBilinear(float x, float y, bool discChannel)
        {
            x = Mathf.Clamp(x, 0f, Width - 1.001f);
            y = Mathf.Clamp(y, 0f, Height - 1.001f);

            int x0 = (int)x;
            int y0 = (int)y;
            int x1 = Mathf.Min(x0 + 1, Width - 1);
            int y1 = Mathf.Min(y0 + 1, Height - 1);

            float fx = x - x0;
            float fy = y - y0;

            float v00 = discChannel ? DiscChannel(x0, y0) : GreenChannel(x0, y0);
            float v10 = discChannel ? DiscChannel(x1, y0) : GreenChannel(x1, y0);
            float v01 = discChannel ? DiscChannel(x0, y1) : GreenChannel(x0, y1);
            float v11 = discChannel ? DiscChannel(x1, y1) : GreenChannel(x1, y1);

            return Mathf.Lerp(Mathf.Lerp(v00, v10, fx), Mathf.Lerp(v01, v11, fx), fy);
        }

        /// <summary>Bilinear RGB sample, for painting the photograph onto the mesh.</summary>
        public void SampleRgb(float x, float y, out float r, out float g, out float b)
        {
            x = Mathf.Clamp(x, 0f, Width - 1.001f);
            y = Mathf.Clamp(y, 0f, Height - 1.001f);

            int x0 = (int)x;
            int y0 = (int)y;
            int x1 = Mathf.Min(x0 + 1, Width - 1);
            int y1 = Mathf.Min(y0 + 1, Height - 1);

            float fx = x - x0;
            float fy = y - y0;

            float r00, g00, b00, r10, g10, b10, r01, g01, b01, r11, g11, b11;
            GetPixel(x0, y0, out r00, out g00, out b00);
            GetPixel(x1, y0, out r10, out g10, out b10);
            GetPixel(x0, y1, out r01, out g01, out b01);
            GetPixel(x1, y1, out r11, out g11, out b11);

            r = Mathf.Lerp(Mathf.Lerp(r00, r10, fx), Mathf.Lerp(r01, r11, fx), fy);
            g = Mathf.Lerp(Mathf.Lerp(g00, g10, fx), Mathf.Lerp(g01, g11, fx), fy);
            b = Mathf.Lerp(Mathf.Lerp(b00, b10, fx), Mathf.Lerp(b01, b11, fx), fy);
        }

        /// <summary>
        /// Width of the illuminated fundus circle, in pixels.
        ///
        /// <para>A fundus photograph is a bright disc on a black surround, and the
        /// camera's stated field angle describes THAT CIRCLE, not the file it was
        /// saved into. A photograph letterboxed into a wider frame is scaled wrong
        /// by exactly the ratio between the two, and the error is systematic,
        /// silent, and lands in every reported millimetre.</para>
        ///
        /// <para>An image already cropped to the circle has no dark surround, so
        /// the scan returns the full width and is still right.</para>
        /// </summary>
        public float EstimateFieldWidthPixels()
        {
            return MeasureFraming().FieldWidthPixels;
        }

        /// <summary>
        /// Measures the illuminated region and decides whether this photograph
        /// still shows the camera's whole field or has been cropped into.
        ///
        /// <para>The distinction decides whether the stated field angle means
        /// anything. A full-frame fundus photograph is a bright circle on black,
        /// and the camera's "30 degrees" describes that circle. CROP INTO IT --
        /// as everyone does when they want a closer look at the disc, and as
        /// every phone screenshot of a fundus image does -- and the bright region
        /// now fills the frame while spanning far less than 30 degrees. Scaling it
        /// as though it were the full field shrinks every millimetre reported,
        /// which is how a perfectly normal 1.8 mm disc comes out as 1.35 mm and
        /// gets labelled "Small". That is not a rounding error; disc size is the
        /// context a CDR is read against, so it changes the clinical reading.</para>
        ///
        /// <para>The test is simply whether the bright region runs off both edges.
        /// A circle inside the frame leaves dark corners; a crop does not.</para>
        /// </summary>
        public FundusFraming MeasureFraming()
        {
            const float DarkThreshold = 0.06f;

            int minX = Width;
            int maxX = -1;
            int darkPixels = 0;
            int sampled = 0;

            // Every other row: the circle's widest span is many rows tall, so
            // halving the work cannot move the answer by even a pixel.
            for (int y = 0; y < Height; y += 2)
            {
                for (int x = 0; x < Width; x++)
                {
                    sampled++;

                    if (DiscChannel(x, y) <= DarkThreshold) { darkPixels++; continue; }

                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                }
            }

            FundusFraming framing = new FundusFraming();

            if (maxX <= minX)
            {
                framing.FieldWidthPixels = Width;
                framing.DarkFraction = 0f;
                framing.LooksCropped = true;
                return framing;
            }

            framing.FieldWidthPixels = maxX - minX + 1;
            framing.DarkFraction = sampled > 0 ? (float)darkPixels / sampled : 0f;

            // A circle inscribed in its frame leaves about 21% of it dark
            // (1 - pi/4). Well under a tenth means there is no surround to speak
            // of, so whatever the frame holds, it is not the whole field.
            framing.LooksCropped = framing.DarkFraction < 0.08f;

            return framing;
        }

        /// <summary>
        /// Separable box blur of the disc channel, returned as a new image with the
        /// blurred value written to all three channels.
        ///
        /// <para>Used only to find the disc seed. A raw fundus photograph has a
        /// specular fleck somewhere in it more often than not, and an unblurred
        /// argmax lands on that fleck rather than on the disc.</para>
        /// </summary>
        public FundusImage BlurredDiscChannel(int radius)
        {
            radius = Mathf.Clamp(radius, 1, 128);

            float[] horizontal = new float[Width * Height];

            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    float sum = 0f;
                    int count = 0;

                    for (int k = -radius; k <= radius; k++)
                    {
                        int sx = Mathf.Clamp(x + k, 0, Width - 1);
                        sum += DiscChannel(sx, y);
                        count++;
                    }

                    horizontal[y * Width + x] = sum / count;
                }
            }

            FundusImage result = new FundusImage(Width, Height);

            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    float sum = 0f;
                    int count = 0;

                    for (int k = -radius; k <= radius; k++)
                    {
                        int sy = Mathf.Clamp(y + k, 0, Height - 1);
                        sum += horizontal[sy * Width + x];
                        count++;
                    }

                    float v = sum / count;
                    result.SetPixel(x, y, v, v, v);
                }
            }

            return result;
        }
    }
}
