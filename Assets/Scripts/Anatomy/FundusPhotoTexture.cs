// -----------------------------------------------------------------------------
// Ocular -- painting a real fundus photograph onto the generated nerve head.
//
// The mesh is UV-mapped in polar coordinates (u = theta / 2pi, v = r / extent),
// which is what lets the procedural fundus in FundusTextureBuilder stay locked
// to the geometry when the spec changes. A photograph is rectangular and in
// pixels, so it has to be resampled into that same polar frame before it can go
// on the surface.
//
// Doing it in this direction -- destination-driven, one sample of the photograph
// per texel -- rather than splatting pixels forward into the polar map is what
// keeps the result free of holes. Forward-mapping a rectangle into a polar frame
// leaves the outer rings sparse and the centre oversampled.
//
// The photograph is composited OVER the procedural texture rather than replacing
// it. The modelled retina extends further than most photographs cover once the
// disc is anywhere but dead centre, and a texel that samples outside the picture
// has to become something: procedural retina, feathered in, is the only option
// that does not put a hard black ring around the specimen.
//
// The photograph is appearance only. Not one number on screen comes from these
// texels -- the metrics are measured off the height field, which was built from
// the fitted spec. If the projection were subtly misaligned the disc would look
// wrong and every measurement would still be right.
// -----------------------------------------------------------------------------

using UnityEngine;
using Ocular.Core.Anatomy;
using Ocular.Core.Diagnostics;
using Ocular.Core.Imaging;

namespace Ocular.Anatomy
{
    public static class FundusPhotoTexture
    {
        /// <summary>Feather width at the edge of the photograph's coverage, in texels.</summary>
        private const int FeatherTexels = 10;

        /// <summary>
        /// Converts a Unity texture into the imaging pipeline's plain-array form.
        ///
        /// <para>FLIPS Y. Texture2D row 0 is the BOTTOM of the image; FundusImage
        /// row 0 is the top, matching the file on disk and the pixel coordinates a
        /// clinician's traced contour is expressed in. Skip the flip and superior
        /// and inferior swap -- see the header of FundusImage for why that is the
        /// worst of the available mistakes.</para>
        ///
        /// <para>Returns null if the texture is not readable, which is the normal
        /// state of an imported PNG: Read/Write Enabled is off by default and
        /// GetPixels throws. The import window turns it on.</para>
        /// </summary>
        public static FundusImage ToFundusImage(Texture2D texture)
        {
            if (texture == null) return null;

            Color[] pixels;

            try
            {
                pixels = texture.GetPixels();
            }
            catch (UnityException e)
            {
                OLog.Error(LogCat.Anatomy,
                    "Fundus photograph '" + texture.name + "' is not readable, so it cannot be "
                    + "projected. Enable Read/Write in its import settings. (" + e.Message + ")");
                return null;
            }

            int w = texture.width;
            int h = texture.height;

            FundusImage image = new FundusImage(w, h);

            for (int y = 0; y < h; y++)
            {
                int sourceRow = (h - 1 - y) * w;

                for (int x = 0; x < w; x++)
                {
                    Color c = pixels[sourceRow + x];
                    image.SetPixel(x, y, c.r, c.g, c.b);
                }
            }

            return image;
        }

        /// <summary>
        /// Builds the polar fundus texture for a surface, with the photograph
        /// composited over the procedural tissue wherever it reaches.
        /// </summary>
        public static Texture2D Build(OnhSurface surface,
                                      FundusImage photo,
                                      Vector2 discCentrePixels,
                                      ImageCalibration calibration,
                                      Laterality laterality,
                                      int resolution = FundusTextureBuilder.DefaultResolution)
        {
            Texture2D texture = FundusTextureBuilder.Build(surface, resolution);

            if (texture == null || photo == null || calibration == null) return texture;

            resolution = texture.width;

            Color[] pixels = texture.GetPixels();
            float extentMm = surface.RetinaExtentMm;

            for (int y = 0; y < resolution; y++)
            {
                float v = (y + 0.5f) / resolution;
                float rMm = v * extentMm;

                for (int x = 0; x < resolution; x++)
                {
                    float u = (x + 0.5f) / resolution;
                    float theta = u * DiscGeometry.TwoPi;

                    float discX = rMm * Mathf.Cos(theta);
                    float discY = rMm * Mathf.Sin(theta);

                    // Inverse of PolarContour.ToDiscSpace: undo the mirror, then put
                    // y back the way image rows run.
                    float dxPixels = calibration.MmToPixels(laterality.MirrorsX() ? -discX : discX);
                    float dyPixels = -calibration.MmToPixels(discY);

                    float px = discCentrePixels.x + dxPixels;
                    float py = discCentrePixels.y + dyPixels;

                    float coverage = Coverage(photo, px, py);
                    if (coverage <= 0f) continue;

                    float r, g, b;
                    photo.SampleRgb(px, py, out r, out g, out b);

                    int index = y * resolution + x;
                    Color procedural = pixels[index];

                    pixels[index] = Color.Lerp(procedural, new Color(r, g, b, 1f), coverage);
                }
            }

            texture.SetPixels(pixels);
            texture.Apply(true);
            texture.name = "FundusPhotoTexture";

            return texture;
        }

        /// <summary>
        /// How strongly the photograph should show through at a pixel: 1 well
        /// inside the picture, ramping to 0 at its edge, 0 outside.
        /// </summary>
        private static float Coverage(FundusImage photo, float x, float y)
        {
            if (!photo.Contains(x, y)) return 0f;

            float toEdge = Mathf.Min(Mathf.Min(x, y),
                                     Mathf.Min(photo.Width - 1 - x, photo.Height - 1 - y));

            return Mathf.Clamp01(toEdge / FeatherTexels);
        }
    }
}
