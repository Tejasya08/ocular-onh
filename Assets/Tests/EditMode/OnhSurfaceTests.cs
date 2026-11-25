// -----------------------------------------------------------------------------
// Ocular -- the height field must be continuous, finite and correctly scaled.
//
// A discontinuity at either join is not a cosmetic problem. The slope-contour
// method finds the cup margin by looking for the first steep sample walking
// inward; a step at the DISC margin would be detected as a cup edge and every
// CDR in the app would read close to 1.0. So the continuity assertions here are
// guarding the measurement, not the mesh.
// -----------------------------------------------------------------------------

using NUnit.Framework;
using UnityEngine;
using Ocular.Core.Anatomy;

namespace Ocular.Tests
{
    public class OnhSurfaceTests
    {
        private const float Eps = 1e-4f;

        private static OnhSurface Surface(System.Action<OpticNerveHeadSpec> tweak = null)
        {
            OpticNerveHeadSpec spec = OpticNerveHeadSpec.Healthy();
            if (tweak != null)
            {
                // Struct: mutate a local then hand it over.
                OpticNerveHeadSpec local = spec;
                tweak(local);
                spec = local;
            }
            return new OnhSurface(spec);
        }

        [Test]
        public void DiscRadius_AtCardinalMeridians_MatchesAuthoredDiameters()
        {
            OpticNerveHeadSpec spec = OpticNerveHeadSpec.Healthy();
            OnhSurface s = new OnhSurface(spec);

            Assert.AreEqual(spec.DiscDiameterVerticalMm * 0.5f,
                s.DiscRadiusMm(DiscGeometry.HalfPi), 1e-4f,
                "Radius at 90 degrees must be the vertical semi-diameter.");

            Assert.AreEqual(spec.DiscDiameterHorizontalMm * 0.5f,
                s.DiscRadiusMm(0f), 1e-4f,
                "Radius at 0 degrees must be the horizontal semi-diameter.");
        }

        [Test]
        public void HeightField_IsContinuous_AtCupMargin()
        {
            OnhSurface s = Surface();

            for (float theta = 0f; theta < DiscGeometry.TwoPi; theta += 0.37f)
            {
                float cupR = s.CupRadiusMm(theta);
                if (cupR < 0.05f) continue;

                float inside = s.HeightMm(cupR - Eps, theta);
                float outside = s.HeightMm(cupR + Eps, theta);

                Assert.AreEqual(inside, outside, 2e-3f,
                    "Step at the cup margin at theta=" + theta);
            }
        }

        [Test]
        public void HeightField_IsContinuous_AtDiscMargin()
        {
            OnhSurface s = Surface();

            for (float theta = 0f; theta < DiscGeometry.TwoPi; theta += 0.37f)
            {
                float discR = s.DiscRadiusMm(theta);

                float inside = s.HeightMm(discR - Eps, theta);
                float outside = s.HeightMm(discR + Eps, theta);

                Assert.AreEqual(inside, outside, 2e-3f,
                    "Step at the disc margin at theta=" + theta);
            }
        }

        [Test]
        public void CupCentre_SitsAtAuthoredDepth()
        {
            OnhSurface s = Surface();
            float depth = s.Spec.CupDepthMm;

            Assert.AreEqual(-depth, s.HeightMm(0f, 0f), 1e-4f,
                "Height at the cup centre must equal minus the authored depth.");
        }

        [Test]
        public void RetinaOutsideDisc_IsAtZero_WhenNoPpa()
        {
            OnhSurface s = Surface();
            Assert.AreEqual(0f, s.PpaFactor(1.5f, 0f), 1e-6f, "No PPA authored, so no PPA weight.");

            float discR = s.DiscRadiusMm(0f);
            Assert.AreEqual(0f, s.HeightMm(discR + 0.3f, 0f), 1e-5f,
                "Retina beyond the disc is the zero reference.");
        }

        [Test]
        public void Notch_ThinsRimLocally_AndLeavesTheOppositeSideAlone()
        {
            OpticNerveHeadSpec plain = OpticNerveHeadSpec.Healthy();

            OpticNerveHeadSpec notched = plain;
            notched.NotchAngleDeg = 270f;   // inferior
            notched.NotchDepth = 0.8f;
            notched.NotchWidthDeg = 15f;

            OnhSurface a = new OnhSurface(plain);
            OnhSurface b = new OnhSurface(notched);

            float inferior = DiscGeometry.SectorCentre(DiscSector.Inferior);
            float superior = DiscGeometry.SectorCentre(DiscSector.Superior);

            float rimAtNotchPlain = a.DiscRadiusMm(inferior) - a.CupRadiusMm(inferior);
            float rimAtNotchNotched = b.DiscRadiusMm(inferior) - b.CupRadiusMm(inferior);

            Assert.Less(rimAtNotchNotched, rimAtNotchPlain * 0.5f,
                "A 0.8 notch must remove most of the rim at its centre.");

            float rimOppositePlain = a.DiscRadiusMm(superior) - a.CupRadiusMm(superior);
            float rimOppositeNotched = b.DiscRadiusMm(superior) - b.CupRadiusMm(superior);

            Assert.AreEqual(rimOppositePlain, rimOppositeNotched, 1e-3f,
                "A notch 180 degrees away must not change this rim.");
        }

        [Test]
        public void ExtremeSpecs_ProduceNoNaNOrInfinity()
        {
            OpticNerveHeadSpec[] specs =
            {
                Build(0f, 0f, 0.03f, 1f),        // no cup at all
                Build(0.95f, 0.95f, 1.2f, 8f),   // almost total cupping, deep, flat floor
                Build(0.5f, 0.5f, 0.03f, 1f),    // cup shallower than the reference plane
            };

            // Degenerate rim scales on top of the degenerate cups.
            for (int i = 0; i < specs.Length; i++)
            {
                OpticNerveHeadSpec spec = specs[i];
                spec.RimScaleInferior = 0.05f;
                spec.RimScaleTemporal = 2f;
                spec.NotchDepth = 1f;
                spec.NotchWidthDeg = 60f;
                spec.PpaWidthMm = 1.2f;

                OnhSurface s = new OnhSurface(spec);

                for (float theta = 0f; theta < DiscGeometry.TwoPi; theta += 0.21f)
                {
                    for (float r = 0f; r <= s.RetinaExtentMm; r += 0.05f)
                    {
                        float h = s.HeightMm(r, theta);
                        Assert.IsFalse(float.IsNaN(h) || float.IsInfinity(h),
                            "Non-finite height at spec " + i + " r=" + r + " theta=" + theta);

                        float slope = s.SlopeAt(r, theta);
                        Assert.IsFalse(float.IsNaN(slope) || float.IsInfinity(slope),
                            "Non-finite slope at spec " + i + " r=" + r + " theta=" + theta);
                    }
                }
            }
        }

        private static OpticNerveHeadSpec Build(float cdrV, float cdrH, float depth, float flatness)
        {
            OpticNerveHeadSpec s = OpticNerveHeadSpec.Healthy();
            s.CupToDiscVertical = cdrV;
            s.CupToDiscHorizontal = cdrH;
            s.CupDepthMm = depth;
            s.CupFloorFlatness = flatness;
            return s;
        }

        [Test]
        public void Clamped_KeepsCupFloorBelowTheRimLip()
        {
            OpticNerveHeadSpec s = OpticNerveHeadSpec.Healthy();
            s.CupDepthMm = 0f;

            OpticNerveHeadSpec c = s.Clamped();

            Assert.Greater(c.CupDepthMm, OnhSurface.RimLipMm,
                "A cup shallower than its own lip would turn inside out.");
        }
    }
}
