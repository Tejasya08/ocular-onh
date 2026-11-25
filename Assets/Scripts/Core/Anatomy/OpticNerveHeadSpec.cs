// -----------------------------------------------------------------------------
// Ocular — the authored description of one optic nerve head.
//
// This is the GROUND TRUTH of the simulation. Everything the user sees is
// generated from it, and everything the measurement engine reports is derived
// back out of that generated geometry — never read from these fields directly.
// That round trip is the whole point: it is what makes the numbers on screen a
// measurement of a surface rather than an echo of a slider, and it is what the
// EditMode tests assert.
//
// All lengths are MILLIMETRES. Population figures quoted below are the
// conventional ranges used in clinical teaching; they set the slider bounds and
// the defaults, and are not a substitute for a normative database.
// -----------------------------------------------------------------------------

using System;
using UnityEngine;

namespace Ocular.Core.Anatomy
{
    [Serializable]
    public struct OpticNerveHeadSpec
    {
        // --- Disc dimensions -------------------------------------------------
        // Mean vertical disc diameter is about 1.76 mm, horizontal about 1.72 mm,
        // so a normal disc is very slightly taller than it is wide.

        [Tooltip("Vertical disc diameter (mm). Population range roughly 1.2-2.6.")]
        public float DiscDiameterVerticalMm;

        [Tooltip("Horizontal disc diameter (mm).")]
        public float DiscDiameterHorizontalMm;

        // --- Cup -------------------------------------------------------------
        // Authored as DIAMETER ratios, which is what CDR means clinically. The
        // cup ellipse semi-axes are therefore the disc semi-axes multiplied by
        // these, not something derived from an area.

        [Range(0f, 0.95f), Tooltip("Vertical cup-to-disc ratio. This is the CDR that gets quoted clinically.")]
        public float CupToDiscVertical;

        [Range(0f, 0.95f), Tooltip("Horizontal cup-to-disc ratio.")]
        public float CupToDiscHorizontal;

        [Range(0.02f, 1.0f), Tooltip("Depth from retinal surface to cup floor (mm).")]
        public float CupDepthMm;

        [Range(1f, 8f), Tooltip("Cup wall profile. 1 = conical/saucerised, 6+ = flat floor with steep 'punched out' walls.")]
        public float CupFloorFlatness;

        // --- Per-sector neuroretinal rim -------------------------------------
        // Multipliers on the rim width that the CDR alone would produce. 1 leaves
        // the rim as authored; below 1 is diffuse thinning in that sector. These
        // are what let a case violate ISNT without changing the headline CDR much.

        [Range(0.1f, 2f)] public float RimScaleInferior;
        [Range(0.1f, 2f)] public float RimScaleSuperior;
        [Range(0.1f, 2f)] public float RimScaleNasal;
        [Range(0.1f, 2f)] public float RimScaleTemporal;

        // --- Focal notch -----------------------------------------------------
        // Localised rim loss. Distinct from the diffuse thinning above: a notch
        // is narrow, deep, and is the classic normal-tension glaucoma finding.

        [Tooltip("Notch centre, degrees in disc-space (0 temporal, 90 superior, 180 nasal, 270 inferior).")]
        public float NotchAngleDeg;

        [Range(0f, 1f), Tooltip("Fraction of the rim consumed at the notch centre.")]
        public float NotchDepth;

        [Range(2f, 60f), Tooltip("Angular half-width (sigma) of the notch, degrees.")]
        public float NotchWidthDeg;

        // --- Peripapillary atrophy -------------------------------------------

        [Range(0f, 1.2f), Tooltip("Beta-zone PPA crescent width (mm). 0 disables it.")]
        public float PpaWidthMm;

        [Tooltip("PPA crescent centre, degrees in disc-space.")]
        public float PpaAngleDeg;

        [Range(10f, 360f), Tooltip("Angular extent of the PPA crescent, degrees.")]
        public float PpaArcDeg;

        // --- Disc haemorrhage ------------------------------------------------

        [Tooltip("Splinter (Drance) haemorrhage at the disc margin.")]
        public bool HasDiscHaemorrhage;

        [Tooltip("Haemorrhage centre, degrees in disc-space.")]
        public float HaemorrhageAngleDeg;

        /// <summary>
        /// A healthy average disc. Used as the starting point for every preset so
        /// that a case description only has to state what makes it abnormal.
        /// </summary>
        public static OpticNerveHeadSpec Healthy()
        {
            return new OpticNerveHeadSpec
            {
                DiscDiameterVerticalMm = 1.76f,
                DiscDiameterHorizontalMm = 1.72f,
                CupToDiscVertical = 0.30f,
                CupToDiscHorizontal = 0.32f,
                CupDepthMm = 0.28f,
                CupFloorFlatness = 2.5f,

                // Healthy rim obeys ISNT: inferior thickest, then superior, nasal,
                // temporal thinnest. Encoding it here means the default case passes
                // the ISNT check for the right reason rather than by accident.
                RimScaleInferior = 1.18f,
                RimScaleSuperior = 1.08f,
                RimScaleNasal = 0.98f,
                RimScaleTemporal = 0.80f,

                NotchAngleDeg = 270f,
                NotchDepth = 0f,
                NotchWidthDeg = 18f,

                PpaWidthMm = 0f,
                PpaAngleDeg = 0f,
                PpaArcDeg = 120f,

                HasDiscHaemorrhage = false,
                HaemorrhageAngleDeg = 270f
            };
        }

        /// <summary>
        /// Clamps every field into a physically meaningful range.
        ///
        /// <para>Called before the surface is built rather than trusted from the
        /// inspector. A zero disc diameter divides by zero deep inside the mesh
        /// builder and produces NaN vertices, which Unity renders as an invisible
        /// mesh with a corrupt bounding box — a failure with no error message
        /// anywhere. Clamping here turns that into a merely wrong-looking disc.</para>
        /// </summary>
        public OpticNerveHeadSpec Clamped()
        {
            OpticNerveHeadSpec s = this;

            s.DiscDiameterVerticalMm = Mathf.Clamp(s.DiscDiameterVerticalMm, 0.8f, 3.2f);
            s.DiscDiameterHorizontalMm = Mathf.Clamp(s.DiscDiameterHorizontalMm, 0.8f, 3.2f);

            s.CupToDiscVertical = Mathf.Clamp(s.CupToDiscVertical, 0f, 0.95f);
            s.CupToDiscHorizontal = Mathf.Clamp(s.CupToDiscHorizontal, 0f, 0.95f);

            // The floor must sit below the rim lip or the cup inverts.
            s.CupDepthMm = Mathf.Clamp(s.CupDepthMm, OnhSurface.RimLipMm + 0.01f, 1.2f);
            s.CupFloorFlatness = Mathf.Clamp(s.CupFloorFlatness, 1f, 8f);

            s.RimScaleInferior = Mathf.Clamp(s.RimScaleInferior, 0.05f, 2f);
            s.RimScaleSuperior = Mathf.Clamp(s.RimScaleSuperior, 0.05f, 2f);
            s.RimScaleNasal = Mathf.Clamp(s.RimScaleNasal, 0.05f, 2f);
            s.RimScaleTemporal = Mathf.Clamp(s.RimScaleTemporal, 0.05f, 2f);

            s.NotchDepth = Mathf.Clamp01(s.NotchDepth);
            s.NotchWidthDeg = Mathf.Clamp(s.NotchWidthDeg, 2f, 60f);
            s.NotchAngleDeg = Mathf.Repeat(s.NotchAngleDeg, 360f);

            s.PpaWidthMm = Mathf.Clamp(s.PpaWidthMm, 0f, 1.2f);
            s.PpaArcDeg = Mathf.Clamp(s.PpaArcDeg, 10f, 360f);
            s.PpaAngleDeg = Mathf.Repeat(s.PpaAngleDeg, 360f);

            s.HaemorrhageAngleDeg = Mathf.Repeat(s.HaemorrhageAngleDeg, 360f);

            return s;
        }
    }
}
