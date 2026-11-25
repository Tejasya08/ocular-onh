// -----------------------------------------------------------------------------
// Ocular -- peripapillary retinal nerve fibre layer thickness.
//
// The TSNIT profile: RNFL thickness sampled around a circle concentric with the
// disc, plotted Temporal-Superior-Nasal-Inferior-Temporal. Its shape is the
// "double hump" -- thick at the superior and inferior poles where the arcuate
// bundles crowd in, thin nasally and thinnest temporally. Glaucoma flattens the
// humps, and it does so BEFORE the visual field changes, which is the whole
// reason OCT is worth doing.
//
// APPROXIMATION, stated plainly: this maps a peripapillary angle to the disc
// sector at the SAME angle. Real nerve fibre bundles arc, so a bundle entering
// the disc inferiorly occupies a slightly rotated position on the scan circle.
// The first-order identity mapping preserves what matters for teaching -- that
// local rim loss shows up as local RNFL loss at the same clock hour -- without
// pretending to a fidelity the rest of the model could not support anyway.
// -----------------------------------------------------------------------------

using UnityEngine;
using Ocular.Core.Anatomy;

namespace Ocular.Core.Measurement
{
    public enum RnflClassification
    {
        WithinNormalLimits,
        Borderline,
        OutsideNormalLimits
    }

    public sealed class RnflProfile
    {
        /// <summary>Diameter of the peripapillary scan circle, mm. 3.4 is the OCT convention.</summary>
        public const float ScanCircleDiameterMm = 3.4f;

        /// <summary>
        /// RNFL floor, microns.
        ///
        /// <para>OCT never reads zero, even in an eye with no light perception: glial
        /// tissue and residual structure keep about 45 microns on the scan. A model
        /// that let thickness fall to zero would imply a measurement that can keep
        /// discriminating damage after the nerve is dead -- the real instrument
        /// cannot, and that "floor effect" is a genuine limitation of OCT in advanced
        /// disease that a learner should meet here.</para>
        /// </summary>
        public const float MeasurementFloorUm = 45f;

        // Healthy TSNIT shape. Base plus two Gaussians; the inferior hump is the
        // taller one, matching the usual I > S > N > T ordering of RNFL thickness.
        private const float HealthyBaseUm = 55f;
        private const float SuperiorHumpUm = 75f;
        private const float InferiorHumpUm = 85f;
        private const float HumpSigmaDeg = 35f;

        // Illustrative bands only. A real instrument compares against an
        // age-matched normative database that this project does not have and must
        // not pretend to; the UI says so next to the label.
        private const float BorderlineAverageUm = 90f;
        private const float AbnormalAverageUm = 80f;

        private static OnhSurface _healthyReference;

        private readonly float[] _thicknessUm;
        private readonly float[] _healthyUm;
        private readonly float[] _quadrantAverageUm = new float[4];

        public int SampleCount { get { return _thicknessUm.Length; } }
        public float[] ThicknessUm { get { return _thicknessUm; } }
        public float[] HealthyReferenceUm { get { return _healthyUm; } }
        public float[] QuadrantAverageUm { get { return _quadrantAverageUm; } }
        public float AverageUm { get; private set; }
        public float HealthyAverageUm { get; private set; }

        private RnflProfile(int samples)
        {
            _thicknessUm = new float[samples];
            _healthyUm = new float[samples];
        }

        /// <summary>Angle in disc-space of sample i, radians.</summary>
        public float ThetaAt(int i)
        {
            return i * DiscGeometry.TwoPi / _thicknessUm.Length;
        }

        /// <summary>Healthy TSNIT thickness at theta, microns, before any damage.</summary>
        public static float HealthyThicknessUm(float theta)
        {
            float sigma = HumpSigmaDeg * Mathf.Deg2Rad;

            float dSup = DiscGeometry.AngleDelta(theta, DiscGeometry.SectorCentre(DiscSector.Superior)) / sigma;
            float dInf = DiscGeometry.AngleDelta(theta, DiscGeometry.SectorCentre(DiscSector.Inferior)) / sigma;

            return HealthyBaseUm
                 + SuperiorHumpUm * Mathf.Exp(-0.5f * dSup * dSup)
                 + InferiorHumpUm * Mathf.Exp(-0.5f * dInf * dInf);
        }

        /// <summary>
        /// Derives an RNFL profile from the nerve head's own geometry.
        ///
        /// <para>Thickness is driven by how much rim survives at each angle relative
        /// to a healthy disc at the same angle, then floored. Tying it to the rim
        /// rather than to the authored CDR is the same discipline the rest of the
        /// measurement layer follows: a notch put into the geometry has to show up
        /// here without anything telling this code that a notch exists.</para>
        /// </summary>
        public static RnflProfile Measure(OnhSurface surface, int samples = 256)
        {
            samples = Mathf.Max(16, samples);
            RnflProfile p = new RnflProfile(samples);

            if (surface == null) return p;

            if (_healthyReference == null)
            {
                _healthyReference = new OnhSurface(OpticNerveHeadSpec.Healthy());
            }

            float[] quadrantSum = new float[4];
            int[] quadrantCount = new int[4];

            float total = 0f;
            float healthyTotal = 0f;

            for (int i = 0; i < samples; i++)
            {
                float theta = p.ThetaAt(i);

                float healthy = HealthyThicknessUm(theta);
                p._healthyUm[i] = healthy;
                healthyTotal += healthy;

                float survival = RimSurvival(surface, _healthyReference, theta);

                // Interpolate between the floor and the healthy value. Total rim loss
                // lands on the floor, not on zero.
                float thickness = MeasurementFloorUm + (healthy - MeasurementFloorUm) * survival;
                thickness = Mathf.Max(MeasurementFloorUm, thickness);

                p._thicknessUm[i] = thickness;
                total += thickness;

                int sector = (int)DiscGeometry.SectorAt(theta);
                quadrantSum[sector] += thickness;
                quadrantCount[sector]++;
            }

            p.AverageUm = total / samples;
            p.HealthyAverageUm = healthyTotal / samples;

            for (int s = 0; s < 4; s++)
            {
                p._quadrantAverageUm[s] = quadrantCount[s] > 0 ? quadrantSum[s] / quadrantCount[s] : 0f;
            }

            return p;
        }

        /// <summary>
        /// Fraction of healthy rim tissue surviving at theta, 0..1.
        /// Compares rim-to-disc ratio against the healthy reference disc so that a
        /// large disc with a proportionally large cup is not read as damaged.
        /// </summary>
        private static float RimSurvival(OnhSurface surface, OnhSurface healthy, float theta)
        {
            float discR = surface.DiscRadiusMm(theta);
            if (discR <= 1e-6f) return 0f;
            float ratio = (discR - surface.CupRadiusMm(theta)) / discR;

            float healthyDiscR = healthy.DiscRadiusMm(theta);
            if (healthyDiscR <= 1e-6f) return 1f;
            float healthyRatio = (healthyDiscR - healthy.CupRadiusMm(theta)) / healthyDiscR;

            if (healthyRatio <= 1e-6f) return 1f;
            return Mathf.Clamp01(ratio / healthyRatio);
        }

        public float QuadrantUm(DiscSector sector)
        {
            return _quadrantAverageUm[(int)sector];
        }

        /// <summary>
        /// Overall classification against the illustrative bands. Not a normative
        /// database lookup -- see the constants.
        /// </summary>
        public RnflClassification Classify()
        {
            if (AverageUm >= BorderlineAverageUm) return RnflClassification.WithinNormalLimits;
            if (AverageUm >= AbnormalAverageUm) return RnflClassification.Borderline;
            return RnflClassification.OutsideNormalLimits;
        }

        /// <summary>Fraction of healthy thickness surviving at sample i, 0..1. Drives the field model.</summary>
        public float SurvivalAt(int i)
        {
            float healthy = _healthyUm[i];
            if (healthy <= MeasurementFloorUm) return 1f;
            return Mathf.Clamp01((_thicknessUm[i] - MeasurementFloorUm) / (healthy - MeasurementFloorUm));
        }

        /// <summary>Fraction of healthy thickness surviving at an arbitrary angle, 0..1.</summary>
        public float SurvivalAtTheta(float theta)
        {
            if (_thicknessUm.Length == 0) return 1f;

            float t = Mathf.Repeat(theta, DiscGeometry.TwoPi) / DiscGeometry.TwoPi * _thicknessUm.Length;
            int i = Mathf.Clamp(Mathf.RoundToInt(t), 0, _thicknessUm.Length - 1) % _thicknessUm.Length;
            return SurvivalAt(i);
        }
    }
}
