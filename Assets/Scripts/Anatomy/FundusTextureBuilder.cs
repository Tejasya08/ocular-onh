// -----------------------------------------------------------------------------
// Ocular -- the fundus appearance, generated rather than painted.
//
// Parameterised in exactly the same polar coordinates the mesh UVs use
// (u = theta / 2pi, v = radius / retinaExtent), so the texture and the geometry
// it depicts cannot drift apart when the spec changes.
//
// Generated ONCE PER CASE, never per frame. It is a few hundred thousand texels
// of managed loop; running it on a slider drag would drop the frame rate through
// the floor. Geometry rebuilds every frame, appearance does not.
//
// A word on why the vessels matter: a clinician does not read the cup margin off
// colour alone, they read it off where the vessels BEND as they dive over the
// edge. Drawing vessels that actually bend at the cup margin is therefore not
// decoration - it is the cue the slope-contour method is imitating numerically.
// -----------------------------------------------------------------------------

using UnityEngine;
using Ocular.Core.Anatomy;

namespace Ocular.Anatomy
{
    public static class FundusTextureBuilder
    {
        public const int DefaultResolution = 512;

        // Four major arcades: superotemporal, inferotemporal, superonasal,
        // inferonasal. Angles are disc-space (0 temporal, 90 superior).
        private static readonly float[] ArcadeStartDeg = { 65f, 295f, 115f, 245f };
        private static readonly float[] ArcadeCurveDeg = { -55f, 55f, 40f, -40f };
        private static readonly float[] ArcadeWidth = { 1.0f, 1.1f, 0.7f, 0.75f };

        /// <summary>
        /// Builds the fundus texture for a surface. u spans the meridian, v spans
        /// radius from the disc centre out to RetinaExtentMm.
        /// </summary>
        public static Texture2D Build(OnhSurface surface, int resolution = DefaultResolution)
        {
            if (surface == null) return null;

            resolution = Mathf.Clamp(resolution, 64, 2048);

            Texture2D tex = new Texture2D(resolution, resolution, TextureFormat.RGBA32, true);
            tex.name = "FundusTexture";

            // Wrap in u because u = 0 and u = 1 are the same meridian; clamp in v so
            // the outermost ring does not bleed back to the disc centre.
            tex.wrapModeU = TextureWrapMode.Repeat;
            tex.wrapModeV = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;

            float extentMm = surface.RetinaExtentMm;
            OpticNerveHeadSpec spec = surface.Spec;

            Color[] pixels = new Color[resolution * resolution];

            float haemCentreRad = spec.HaemorrhageAngleDeg * Mathf.Deg2Rad;

            for (int y = 0; y < resolution; y++)
            {
                float v = (y + 0.5f) / resolution;
                float rMm = v * extentMm;

                for (int x = 0; x < resolution; x++)
                {
                    float u = (x + 0.5f) / resolution;
                    float theta = u * DiscGeometry.TwoPi;

                    float discR = surface.DiscRadiusMm(theta);
                    float cupR = surface.CupRadiusMm(theta);

                    Color c = BaseTissue(surface, spec, rMm, theta, discR, cupR, extentMm);
                    c = ApplyVessels(c, rMm, theta, discR, cupR, extentMm);

                    if (spec.HasDiscHaemorrhage)
                    {
                        c = ApplyHaemorrhage(c, rMm, theta, discR, haemCentreRad);
                    }

                    pixels[y * resolution + x] = c;
                }
            }

            tex.SetPixels(pixels);
            tex.Apply(true);
            return tex;
        }

        private static Color BaseTissue(OnhSurface surface, OpticNerveHeadSpec spec,
                                        float rMm, float theta, float discR, float cupR,
                                        float extentMm)
        {
            if (rMm >= discR)
            {
                // Retina, with a gentle darkening toward the periphery so the disc
                // reads as the brightest thing in frame, as it does on a real photo.
                float outward = Mathf.InverseLerp(discR, extentMm, rMm);
                Color retina = Color.Lerp(FundusPalette.Retina, FundusPalette.RetinaDark, outward * 0.7f);

                float ppa = surface.PpaFactor(rMm, theta, discR);
                return Color.Lerp(retina, FundusPalette.Ppa, ppa);
            }

            if (rMm >= cupR)
            {
                // Rim. Pales slightly toward the cup margin: the rim thins as it
                // rolls over, so less tissue sits in front of the lamina.
                float span = Mathf.Max(1e-5f, discR - cupR);
                float intoRim = Mathf.Clamp01((discR - rMm) / span);
                return Color.Lerp(FundusPalette.Rim, FundusPalette.CupFloor, intoRim * 0.35f);
            }

            // Cup floor, with lamina cribrosa pores that only become visible once
            // the cup is deep enough to expose them - the "laminar dot sign".
            float laminaVisibility = Mathf.InverseLerp(0.25f, 0.6f, spec.CupDepthMm);
            if (laminaVisibility <= 0f) return FundusPalette.CupFloor;

            float pores = PoreNoise(rMm, theta);
            return Color.Lerp(FundusPalette.CupFloor, FundusPalette.Lamina, pores * laminaVisibility);
        }

        /// <summary>
        /// Cheap deterministic pore pattern. Deterministic matters: the texture is
        /// regenerated whenever the case changes and a random pattern would make the
        /// same disc look different every time it was selected.
        /// </summary>
        private static float PoreNoise(float rMm, float theta)
        {
            float px = rMm * Mathf.Cos(theta) * 28f;
            float py = rMm * Mathf.Sin(theta) * 28f;

            float n = Mathf.PerlinNoise(px + 13.7f, py + 4.2f);
            return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.55f, 0.85f, n));
        }

        /// <summary>
        /// Draws the arcades. Each vessel bends as it crosses the cup margin, which
        /// is the cue a clinician actually uses to call the cup edge.
        /// </summary>
        private static Color ApplyVessels(Color c, float rMm, float theta,
                                          float discR, float cupR, float extentMm)
        {
            for (int i = 0; i < ArcadeStartDeg.Length; i++)
            {
                float start = ArcadeStartDeg[i] * Mathf.Deg2Rad;
                float curve = ArcadeCurveDeg[i] * Mathf.Deg2Rad;

                // Outside the disc the vessel sweeps away along the arcade; inside
                // it runs almost radially toward the centre.
                float outward = Mathf.Clamp01((rMm - cupR) / Mathf.Max(1e-4f, extentMm - cupR));
                float vesselTheta = start + curve * outward * outward;

                float dAng = DiscGeometry.AngleDelta(theta, vesselTheta);

                // Constant ARC width, so the vessel does not fan out with radius.
                float arcMm = dAng * Mathf.Max(rMm, 0.05f);

                // Vessels taper as they run peripherally.
                float halfWidthMm = 0.035f * ArcadeWidth[i] * Mathf.Lerp(1.15f, 0.5f, outward);

                if (arcMm > halfWidthMm * 2f) continue;

                float edge = 1f - Mathf.SmoothStep(halfWidthMm * 0.6f, halfWidthMm * 2f, arcMm);
                if (edge <= 0f) continue;

                // Alternating artery/vein, arteries lighter and narrower.
                Color vessel = (i % 2 == 0) ? FundusPalette.VesselArtery : FundusPalette.Vessel;

                // Vessels disappear where they dive over the cup edge onto the floor.
                if (rMm < cupR) edge *= 0.35f;

                c = Color.Lerp(c, vessel, edge);
            }

            return c;
        }

        private static Color ApplyHaemorrhage(Color c, float rMm, float theta,
                                              float discR, float centreRad)
        {
            // Splinter haemorrhages sit ON the disc margin and run radially, in the
            // orientation of the nerve fibre layer.
            float dAng = DiscGeometry.AngleDelta(theta, centreRad);
            if (dAng > 0.22f) return c;

            float radialCentre = discR * 0.94f;
            float dR = Mathf.Abs(rMm - radialCentre);
            if (dR > 0.13f) return c;

            float a = (1f - Mathf.SmoothStep(0.06f, 0.22f, dAng))
                    * (1f - Mathf.SmoothStep(0.03f, 0.13f, dR));

            return Color.Lerp(c, FundusPalette.Haemorrhage, Mathf.Clamp01(a));
        }
    }
}
