// -----------------------------------------------------------------------------
// Ocular — shared conventions for optic-nerve-head geometry.
//
// ANGLE CONVENTION (used by every file in this project):
//
//     theta is measured in the optic disc's OWN frame, in radians:
//         0      = TEMPORAL
//         pi/2   = SUPERIOR
//         pi     = NASAL
//         3pi/2  = INFERIOR
//
// Keeping the maths in the disc's own frame is what makes laterality a
// presentation concern rather than a modelling one. In a fundus photograph the
// nasal side of a RIGHT eye and a LEFT eye appear on opposite sides of the
// image; if that mirroring leaked into the geometry, every sector-based
// measurement (ISNT especially) would silently swap nasal and temporal for one
// eye. Instead the model is always authored in disc-space and the renderer
// mirrors X for OS. See Laterality.MirrorsX.
//
// UNITS: millimetres, everywhere in this assembly. Nothing here knows about
// Unity world units. The conversion to metres for display happens once, in the
// mesh builder, via OcularScale.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace Ocular.Core.Anatomy
{
    /// <summary>Which eye. Affects presentation only, never the maths.</summary>
    public enum Laterality
    {
        /// <summary>Oculus dexter — right eye.</summary>
        OD,

        /// <summary>Oculus sinister — left eye.</summary>
        OS
    }

    /// <summary>
    /// The four clinical disc sectors, in the order the ISNT rule names them.
    /// Values are deliberately 0..3 so they can index arrays directly.
    /// </summary>
    public enum DiscSector
    {
        Inferior = 0,
        Superior = 1,
        Nasal = 2,
        Temporal = 3
    }

    /// <summary>
    /// Disc size bands. Cup-to-disc ratio cannot be read without knowing this:
    /// a large disc carries a large cup with a perfectly normal amount of rim
    /// tissue, and a small disc can be severely damaged at a CDR that looks
    /// reassuring. This is the single most common misreading of a CDR figure.
    /// </summary>
    public enum DiscSizeClass
    {
        Small,
        Average,
        Large
    }

    public static class DiscGeometry
    {
        public const float TwoPi = 6.28318530718f;
        public const float HalfPi = 1.57079632679f;

        /// <summary>Centre angle of each sector, radians, in disc-space.</summary>
        public static float SectorCentre(DiscSector sector)
        {
            switch (sector)
            {
                case DiscSector.Temporal: return 0f;
                case DiscSector.Superior: return HalfPi;
                case DiscSector.Nasal: return Mathf.PI;
                default: return 3f * HalfPi; // Inferior
            }
        }

        /// <summary>
        /// Radius of an ellipse with the given semi-axes at angle theta.
        /// </summary>
        public static float EllipseRadius(float semiX, float semiY, float theta)
        {
            float c = Mathf.Cos(theta);
            float s = Mathf.Sin(theta);
            float denom = Mathf.Sqrt(semiY * semiY * c * c + semiX * semiX * s * s);
            return denom < 1e-6f ? 0f : (semiX * semiY) / denom;
        }

        /// <summary>
        /// Smooth weight of the sector centred at <paramref name="centre"/>, for
        /// blending per-sector parameters around the disc.
        ///
        /// <para>cos-squared lobes, clamped to the near half. At any angle exactly
        /// two adjacent lobes are non-zero and they sum to cos^2 + sin^2 = 1, so
        /// this is an exact partition of unity with no normalisation step and no
        /// seam. Hard 90-degree sector boundaries would put a visible crease in
        /// the rim every quarter turn and a discontinuity in the measured rim
        /// width profile.</para>
        /// </summary>
        public static float SectorWeight(float theta, float centre)
        {
            float d = Mathf.Cos(theta - centre);
            return d > 0f ? d * d : 0f;
        }

        /// <summary>
        /// Blends four per-sector values into a smooth function of theta.
        /// </summary>
        public static float BlendSectors(float theta, float inferior, float superior, float nasal, float temporal)
        {
            return SectorWeight(theta, SectorCentre(DiscSector.Inferior)) * inferior
                 + SectorWeight(theta, SectorCentre(DiscSector.Superior)) * superior
                 + SectorWeight(theta, SectorCentre(DiscSector.Nasal)) * nasal
                 + SectorWeight(theta, SectorCentre(DiscSector.Temporal)) * temporal;
        }

        /// <summary>
        /// Smallest absolute angle between two directions, radians, in [0, pi].
        /// </summary>
        public static float AngleDelta(float a, float b)
        {
            float d = Mathf.Repeat(a - b, TwoPi);
            if (d > Mathf.PI) d -= TwoPi;
            return Mathf.Abs(d);
        }

        /// <summary>
        /// Which sector an angle falls in, by nearest sector centre.
        /// </summary>
        public static DiscSector SectorAt(float theta)
        {
            DiscSector best = DiscSector.Inferior;
            float bestDelta = float.MaxValue;

            for (int i = 0; i < 4; i++)
            {
                DiscSector s = (DiscSector)i;
                float d = AngleDelta(theta, SectorCentre(s));
                if (d < bestDelta) { bestDelta = d; best = s; }
            }

            return best;
        }

        /// <summary>
        /// Classifies disc size by vertical disc diameter. Bands follow the
        /// conventional small / average / large split used alongside the DDLS.
        /// </summary>
        public static DiscSizeClass ClassifyDiscSize(float verticalDiameterMm)
        {
            if (verticalDiameterMm < 1.5f) return DiscSizeClass.Small;
            if (verticalDiameterMm > 2.0f) return DiscSizeClass.Large;
            return DiscSizeClass.Average;
        }

        /// <summary>True if this eye is drawn mirrored in X relative to disc-space.</summary>
        public static bool MirrorsX(this Laterality laterality) => laterality == Laterality.OS;
    }
}
