// -----------------------------------------------------------------------------
// Ocular -- the optic nerve head as a continuous height field.
//
// This class is the ONLY place the authored spec turns into geometry. The mesh
// builder tessellates it, the cross-section overlay traces it, and the
// measurement engine reads its numbers back out; all three see exactly the same
// surface, which is what makes a measured CDR a measurement rather than a
// restatement of the slider that produced it.
//
// It is a pure evaluator: no Unity objects, no allocation, no state beyond the
// clamped spec and a handful of derived constants. Callers hammer it (720
// meridians x 256 radial samples per measurement pass), so everything that can
// be precomputed in the constructor is.
//
// Height sign convention: 0 is the retinal surface, NEGATIVE is into the cup.
// Millimetres throughout.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace Ocular.Core.Anatomy
{
    public sealed class OnhSurface
    {
        /// <summary>
        /// Drop from the retinal surface to the cup margin, mm.
        ///
        /// <para>The rim is not a flat shelf: it rolls over into the cup. This lip
        /// is the depth of that roll-over, and it is what the rim/cup junction
        /// height is defined as. It also sets the floor under CupDepthMm in
        /// OpticNerveHeadSpec.Clamped() -- a cup shallower than its own lip would
        /// turn inside out and the wall would face the wrong way.</para>
        /// </summary>
        public const float RimLipMm = 0.02f;

        /// <summary>Cup radius is never allowed closer to the disc margin than this fraction.</summary>
        public const float MaxCupFraction = 0.98f;

        // Beta-zone PPA is a window onto sclera, not a crater. 15 microns is
        // enough for the depression to read on the cross-section trace and in the
        // normals without putting a visible step at the disc margin.
        private const float PpaDepthMm = 0.015f;

        // Below this a "cup" is a point, not a basin. r/cupR would be an infinity
        // and every vertex inside it would come out NaN -- which Unity renders as
        // an invisible mesh with a corrupt bounding box and no error message.
        private const float MinCupRadiusMm = 1e-4f;

        // Central-difference step for SlopeAt. 5 microns is fine enough to place
        // the cup margin to well under the eye's own resolution and coarse enough
        // that float cancellation in the subtraction is not a factor.
        private const float SlopeStepMm = 0.005f;

        private readonly OpticNerveHeadSpec _spec;

        private readonly float _discSemiX;   // horizontal semi-axis (temporal-nasal)
        private readonly float _discSemiY;   // vertical semi-axis (superior-inferior)
        private readonly float _cupSemiX;
        private readonly float _cupSemiY;

        private readonly bool _hasNotch;
        private readonly float _notchCentreRad;
        private readonly float _notchSigmaRad;

        private readonly bool _hasPpa;
        private readonly float _ppaCentreRad;
        private readonly float _ppaHalfArcRad;

        private readonly float _retinaExtentMm;

        public OnhSurface(OpticNerveHeadSpec spec)
        {
            // Clamp on the way in, not at every call site. Everything downstream is
            // then free to assume a physically meaningful spec.
            _spec = spec.Clamped();

            _discSemiX = _spec.DiscDiameterHorizontalMm * 0.5f;
            _discSemiY = _spec.DiscDiameterVerticalMm * 0.5f;

            // CDR is authored as a DIAMETER ratio, so it scales the semi-axes directly.
            _cupSemiX = _discSemiX * _spec.CupToDiscHorizontal;
            _cupSemiY = _discSemiY * _spec.CupToDiscVertical;

            _hasNotch = _spec.NotchDepth > 0f;
            _notchCentreRad = _spec.NotchAngleDeg * Mathf.Deg2Rad;
            _notchSigmaRad = Mathf.Max(_spec.NotchWidthDeg * Mathf.Deg2Rad, 1e-3f);

            _hasPpa = _spec.PpaWidthMm > 0f;
            _ppaCentreRad = _spec.PpaAngleDeg * Mathf.Deg2Rad;
            _ppaHalfArcRad = Mathf.Max(_spec.PpaArcDeg * 0.5f * Mathf.Deg2Rad, 1e-3f);

            // Far enough out that the PPA crescent and a margin of ordinary retina
            // beyond it are inside the modelled area even for a 3.2 mm disc with a
            // 1.2 mm crescent; otherwise a large case would have its atrophy sliced
            // off by the edge of the mesh.
            float maxDiscSemi = Mathf.Max(_discSemiX, _discSemiY);
            _retinaExtentMm = Mathf.Max(2.6f, maxDiscSemi + _spec.PpaWidthMm + 0.5f);
        }

        /// <summary>The clamped spec this surface was built from.</summary>
        public OpticNerveHeadSpec Spec { get { return _spec; } }

        /// <summary>Radius out to which the height field is defined, mm.</summary>
        public float RetinaExtentMm { get { return _retinaExtentMm; } }

        /// <summary>Disc margin radius at theta, mm. Disc-space angle (0 = temporal).</summary>
        public float DiscRadiusMm(float theta)
        {
            return DiscGeometry.EllipseRadius(_discSemiX, _discSemiY, theta);
        }

        /// <summary>
        /// Geometric cup margin radius at theta, mm.
        ///
        /// <para>Built rim-first rather than cup-first. The CDR ellipse only gives a
        /// STARTING rim width; the per-sector scales and the notch then eat into
        /// that width, and the cup is whatever is left over. Doing it the other way
        /// round -- scaling the cup -- would make a notch widen the cup everywhere
        /// on that side instead of biting a wedge out of the rim.</para>
        /// </summary>
        public float CupRadiusMm(float theta)
        {
            float discR = DiscRadiusMm(theta);
            float baseCupR = DiscGeometry.EllipseRadius(_cupSemiX, _cupSemiY, theta);
            float baseRim = discR - baseCupR;

            float rim = baseRim
                      * DiscGeometry.BlendSectors(theta,
                            _spec.RimScaleInferior, _spec.RimScaleSuperior,
                            _spec.RimScaleNasal, _spec.RimScaleTemporal)
                      * NotchFactor(theta);

            // The upper clamp leaves a sliver of rim even at a total notch. A cup
            // that reached the disc margin exactly would give the height field a
            // zero-width rim band to interpolate across.
            return Mathf.Clamp(discR - rim, 0f, discR * MaxCupFraction);
        }

        /// <summary>
        /// Multiplier the focal notch applies to the rim width at theta, 0..1.
        /// Gaussian in the angular distance from the notch centre, sigma =
        /// NotchWidthDeg. Exposed because the highlight overlay shades by it.
        /// </summary>
        public float NotchFactor(float theta)
        {
            if (!_hasNotch) return 1f;

            float d = DiscGeometry.AngleDelta(theta, _notchCentreRad) / _notchSigmaRad;
            return 1f - _spec.NotchDepth * Mathf.Exp(-0.5f * d * d);
        }

        /// <summary>
        /// Signed height at (radius, theta), mm. 0 = retinal surface, negative into
        /// the cup.
        /// </summary>
        public float HeightMm(float radiusMm, float theta)
        {
            return HeightMm(radiusMm, theta, DiscRadiusMm(theta), CupRadiusMm(theta));
        }

        /// <summary>
        /// Height with the disc and cup radii for this meridian already known.
        ///
        /// <para>The hot path. A radial scan evaluates one meridian at hundreds of
        /// radii; recomputing the two ellipse radii (four trig calls) per sample
        /// would be the dominant cost of a measurement pass.</para>
        /// </summary>
        public float HeightMm(float radiusMm, float theta, float discRadiusMm, float cupRadiusMm)
        {
            // --- Retina, at and beyond the disc margin --------------------------
            if (radiusMm >= discRadiusMm)
            {
                // PpaFactor is 0 at exactly the disc margin, so this meets the rim
                // branch (which is also 0 there) with no step.
                return -PpaDepthMm * PpaFactor(radiusMm, theta, discRadiusMm);
            }

            // --- Neuroretinal rim ----------------------------------------------
            // Also the branch taken when there is no cup at all, in which case the
            // rim fills the whole disc and bottoms out at the lip depth.
            if (cupRadiusMm <= MinCupRadiusMm || radiusMm >= cupRadiusMm)
            {
                float span = discRadiusMm - cupRadiusMm;
                if (span <= 1e-6f) return -RimLipMm;

                float u = Mathf.Clamp01((radiusMm - cupRadiusMm) / span);
                float w = 1f - u;

                // Squared so the rim meets the retina TANGENTIALLY at u = 1: the
                // slope there is zero, not merely the height. A linear ramp would
                // put a crease all the way round the disc margin that the slope
                // detector would then mistake for a cup edge.
                return -RimLipMm * w * w;
            }

            // --- Cup ------------------------------------------------------------
            float v = Mathf.Clamp01(radiusMm / cupRadiusMm);
            float floorDrop = _spec.CupDepthMm - RimLipMm;   // >= 0.01 mm after Clamped()

            // v^flatness: 1 gives a cone, 6+ a flat floor with a steep punched-out
            // wall. At v = 1 this is exactly -RimLipMm, matching the rim branch at
            // the cup margin; at v = 0 it is exactly -CupDepthMm.
            return -RimLipMm - floorDrop * (1f - Mathf.Pow(v, _spec.CupFloorFlatness));
        }

        /// <summary>d(height)/d(radius) at (radius, theta), by central difference.</summary>
        public float SlopeAt(float radiusMm, float theta)
        {
            return SlopeAt(radiusMm, theta, DiscRadiusMm(theta), CupRadiusMm(theta));
        }

        /// <summary>Slope with the disc and cup radii for this meridian already known.</summary>
        public float SlopeAt(float radiusMm, float theta, float discRadiusMm, float cupRadiusMm)
        {
            float lo = Mathf.Max(0f, radiusMm - SlopeStepMm);
            float hi = radiusMm + SlopeStepMm;
            float span = hi - lo;
            if (span <= 1e-9f) return 0f;

            float hLo = HeightMm(lo, theta, discRadiusMm, cupRadiusMm);
            float hHi = HeightMm(hi, theta, discRadiusMm, cupRadiusMm);
            return (hHi - hLo) / span;
        }

        /// <summary>Peripapillary atrophy weight at (radius, theta), 0..1. For texturing.</summary>
        public float PpaFactor(float radiusMm, float theta)
        {
            return PpaFactor(radiusMm, theta, DiscRadiusMm(theta));
        }

        /// <summary>PPA weight with the disc radius for this meridian already known.</summary>
        public float PpaFactor(float radiusMm, float theta, float discRadiusMm)
        {
            if (!_hasPpa) return 0f;

            // Beta zone starts AT the disc margin and dies out at its own width.
            float t = (radiusMm - discRadiusMm) / _spec.PpaWidthMm;
            if (t <= 0f || t >= 1f) return 0f;

            float dAng = DiscGeometry.AngleDelta(theta, _ppaCentreRad);
            if (dAng >= _ppaHalfArcRad) return 0f;

            // sin^2 radially and cos^2 angularly: both reach 0 with zero gradient at
            // their edges, so the crescent fades out instead of ending in a ridge
            // that the normals would catch as a hard line.
            float radial = Mathf.Sin(Mathf.PI * t);
            radial *= radial;

            float angular = Mathf.Cos(0.5f * Mathf.PI * (dAng / _ppaHalfArcRad));
            angular *= angular;

            return radial * angular;
        }
    }
}
