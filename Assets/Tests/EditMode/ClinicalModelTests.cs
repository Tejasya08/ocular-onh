// -----------------------------------------------------------------------------
// Ocular -- the clinical models, and the one sign error that matters most.
//
// InferiorNotch_ProducesSuperiorFieldDefect exists because the optical inversion
// is the easiest thing in this project to get backwards, and backwards output
// looks entirely plausible. If someone "simplifies" the negation out of
// VisualFieldModel.FieldPointToDiscAngle, this test fails instead of the
// teaching quietly becoming wrong.
// -----------------------------------------------------------------------------

using NUnit.Framework;
using UnityEngine;
using Ocular.Core.Anatomy;
using Ocular.Core.Measurement;
using Ocular.Data;

namespace Ocular.Tests
{
    public class ClinicalModelTests
    {
        // =====================================================================
        // DDLS
        // =====================================================================

        [TestCase(0.45f, 1)]
        [TestCase(0.35f, 2)]
        [TestCase(0.25f, 3)]
        [TestCase(0.15f, 4)]
        [TestCase(0.05f, 5)]
        public void Ddls_RimRatioBands_MapToPublishedStages(float ratio, int expected)
        {
            Assert.AreEqual(expected, DdlsScale.StageForAverageDisc(ratio, 0f));
        }

        [TestCase(30f, 6)]
        [TestCase(60f, 7)]
        [TestCase(120f, 8)]
        [TestCase(200f, 9)]
        [TestCase(300f, 10)]
        public void Ddls_RimAbsence_SupersedesTheRatioBands(float absenceDeg, int expected)
        {
            // Ratio deliberately healthy: absence must win regardless.
            Assert.AreEqual(expected, DdlsScale.StageForAverageDisc(0.45f, absenceDeg));
        }

        [Test]
        public void Ddls_SmallDiscIsWorse_LargeDiscIsBetter()
        {
            Assert.AreEqual(4, DdlsScale.AdjustForDiscSize(3, DiscSizeClass.Small),
                "The same rim ratio on a small disc means less surviving tissue.");
            Assert.AreEqual(2, DdlsScale.AdjustForDiscSize(3, DiscSizeClass.Large));
            Assert.AreEqual(3, DdlsScale.AdjustForDiscSize(3, DiscSizeClass.Average));
        }

        [Test]
        public void Ddls_ClampsToTheScale()
        {
            Assert.AreEqual(1, DdlsScale.AdjustForDiscSize(1, DiscSizeClass.Large));
            Assert.AreEqual(10, DdlsScale.AdjustForDiscSize(10, DiscSizeClass.Small));
        }

        // =====================================================================
        // RNFL
        // =====================================================================

        [Test]
        public void Rnfl_HealthyAverage_LandsInTheNormalBand()
        {
            RnflProfile p = RnflProfile.Measure(new OnhSurface(OpticNerveHeadSpec.Healthy()));

            Assert.That(p.AverageUm, Is.InRange(85f, 105f),
                "A healthy TSNIT average should sit in the usual 90-110 micron range.");
        }

        [Test]
        public void Rnfl_DoubleHump_PeaksVerticallyNotHorizontally()
        {
            RnflProfile p = RnflProfile.Measure(new OnhSurface(OpticNerveHeadSpec.Healthy()));

            float sup = p.QuadrantUm(DiscSector.Superior);
            float inf = p.QuadrantUm(DiscSector.Inferior);
            float nas = p.QuadrantUm(DiscSector.Nasal);
            float tmp = p.QuadrantUm(DiscSector.Temporal);

            Assert.Greater(inf, nas, "Inferior bundle is thicker than nasal.");
            Assert.Greater(inf, tmp, "Inferior bundle is thicker than temporal.");
            Assert.Greater(sup, tmp, "Superior bundle is thicker than temporal.");
            Assert.Greater(inf, sup, "The inferior hump is the taller of the two.");
        }

        [Test]
        public void Rnfl_NeverFallsBelowTheMeasurementFloor()
        {
            RnflProfile p = RnflProfile.Measure(new OnhSurface(Advanced()));

            for (int i = 0; i < p.SampleCount; i++)
            {
                Assert.GreaterOrEqual(p.ThicknessUm[i], RnflProfile.MeasurementFloorUm - 1e-3f,
                    "OCT has a floor effect; thickness must never read below it.");
            }
        }

        // =====================================================================
        // Visual field -- the inversion
        // =====================================================================

        [Test]
        public void FieldPointToDiscAngle_SuperiorField_MapsToInferiorDisc()
        {
            float angle = VisualFieldModel.FieldPointToDiscAngle(0f, 15f);

            Assert.AreEqual(3f * DiscGeometry.HalfPi, angle, 0.01f,
                "A superior field point must map to the INFERIOR disc (270 degrees).");
        }

        [Test]
        public void FieldPointToDiscAngle_InferiorField_MapsToSuperiorDisc()
        {
            float angle = VisualFieldModel.FieldPointToDiscAngle(0f, -15f);

            Assert.AreEqual(DiscGeometry.HalfPi, angle, 0.01f,
                "An inferior field point must map to the SUPERIOR disc (90 degrees).");
        }

        [Test]
        public void InferiorNotch_ProducesSuperiorFieldDefect()
        {
            OpticNerveHeadSpec s = OpticNerveHeadSpec.Healthy();
            s.CupToDiscVertical = 0.7f;
            s.CupToDiscHorizontal = 0.6f;
            s.NotchAngleDeg = 270f;      // INFERIOR rim
            s.NotchDepth = 0.9f;
            s.NotchWidthDeg = 22f;

            VisualFieldResult field = VisualFieldModel.Analyse(
                RnflProfile.Measure(new OnhSurface(s)), Laterality.OD);

            float superiorLoss = MeanDeviationWhere(field, true);
            float inferiorLoss = MeanDeviationWhere(field, false);

            Assert.Less(superiorLoss, inferiorLoss - 1f,
                "An INFERIOR notch must damage the SUPERIOR field. "
                + "superior MD=" + superiorLoss + " inferior MD=" + inferiorLoss);
        }

        private static float MeanDeviationWhere(VisualFieldResult f, bool superiorHemifield)
        {
            float sum = 0f;
            int n = 0;

            for (int i = 0; i < f.Points.Length; i++)
            {
                VisualFieldPoint p = f.Points[i];
                if (p.IsBlindSpot) continue;

                // Skip the central points: the papillomacular bundle is damped on
                // both sides and would dilute the hemifield comparison.
                if (Mathf.Abs(p.YDeg) < 6f) continue;

                if ((p.YDeg > 0f) != superiorHemifield) continue;

                sum += p.DeviationDb;
                n++;
            }

            return n > 0 ? sum / n : 0f;
        }

        [Test]
        public void FieldStaging_FollowsHodappParrishAnderson()
        {
            Assert.AreEqual(FieldStage.Mild, VisualFieldModel.StageFor(-4f));
            Assert.AreEqual(FieldStage.Moderate, VisualFieldModel.StageFor(-9f));
            Assert.AreEqual(FieldStage.Severe, VisualFieldModel.StageFor(-15f));
        }

        [Test]
        public void HealthyDisc_HasEssentiallyNoFieldLoss()
        {
            VisualFieldResult f = VisualFieldModel.Analyse(
                RnflProfile.Measure(new OnhSurface(OpticNerveHeadSpec.Healthy())), Laterality.OD);

            Assert.Greater(f.MeanDeviationDb, -2.5f,
                "A healthy nerve must not manufacture field loss.");
        }

        // =====================================================================
        // IOP
        // =====================================================================

        [Test]
        public void ThickCornea_OverestimatesPressure_SoCorrectionGoesDown()
        {
            float corrected = IopModel.CorrectedMmHg(28f, 610f);
            Assert.Less(corrected, 28f, "A thick cornea reads high; the corrected value must be lower.");
        }

        [Test]
        public void ThinCornea_UnderestimatesPressure_SoCorrectionGoesUp()
        {
            float corrected = IopModel.CorrectedMmHg(15f, 505f);
            Assert.Greater(corrected, 15f, "A thin cornea reads low; the corrected value must be higher.");
        }

        // =====================================================================
        // Case presets must actually show what their vignettes claim
        // =====================================================================

        [Test]
        public void HealthyCase_ObeysIsnt()
        {
            GlaucomaCase c = CasePresets.Healthy();
            OnhMetrics m = Measure(c);

            Assert.IsTrue(m.IsntSatisfied, "Healthy case must obey ISNT: " + m.IsntDetail);
            Object.DestroyImmediate(c);
        }

        [Test]
        public void LargePhysiologicCup_IsTheFalsePositive_HighCdrButNormalRim()
        {
            GlaucomaCase c = CasePresets.LargePhysiologicCup();
            OnhMetrics m = Measure(c);

            Assert.AreEqual(DiscSizeClass.Large, m.DiscSize, "This case is defined by its large disc.");
            Assert.Greater(m.VerticalCdr, 0.5f, "The CDR really is high - that is the trap.");
            Assert.Greater(m.RimAreaMm2, 1.5f, "...but the rim area is normal, which is the answer.");
            Assert.IsTrue(m.IsntSatisfied, "...and the rim ordering is intact: " + m.IsntDetail);

            Object.DestroyImmediate(c);
        }

        [Test]
        public void EarlyPoag_BreaksIsntDespiteAModestCdr()
        {
            GlaucomaCase c = CasePresets.EarlyPoag();
            OnhMetrics m = Measure(c);

            Assert.Less(m.VerticalCdr, 0.7f, "The CDR alone is not alarming here.");
            Assert.IsFalse(m.IsntSatisfied, "But the rim ordering is wrong, which is the finding.");

            Object.DestroyImmediate(c);
        }

        [Test]
        public void NormalTensionCase_HasItsNarrowestRimInferiorly()
        {
            GlaucomaCase c = CasePresets.NormalTensionGlaucoma();
            OnhMetrics m = Measure(c);

            float delta = Mathf.Abs(Mathf.DeltaAngle(m.MinRimAngleDeg, 270f));
            Assert.Less(delta, 35f,
                "The authored notch is inferior, so the narrowest rim must be too. Found " + m.MinRimAngleDeg);

            Object.DestroyImmediate(c);
        }

        [Test]
        public void AdvancedCase_ReachesAHighDdlsStage()
        {
            GlaucomaCase c = CasePresets.AdvancedPoag();
            OnhMetrics m = Measure(c);

            Assert.GreaterOrEqual(m.DdlsStage, 5, "End-stage cupping must stage high. " + m.DdlsLabel);
            Object.DestroyImmediate(c);
        }

        [Test]
        public void EveryPreset_ProducesFiniteMetrics()
        {
            GlaucomaCase[] all = CasePresets.BuildAll();

            for (int i = 0; i < all.Length; i++)
            {
                OnhMetrics m = Measure(all[i]);

                Assert.IsFalse(float.IsNaN(m.VerticalCdr), all[i].DisplayName + " produced NaN CDR");
                Assert.That(m.VerticalCdr, Is.InRange(0f, 1f), all[i].DisplayName + " CDR out of range");
                Assert.Greater(m.DiscAreaMm2, 0f, all[i].DisplayName + " has no disc area");

                Object.DestroyImmediate(all[i]);
            }
        }

        // =====================================================================

        private static OnhMetrics Measure(GlaucomaCase c)
        {
            return OnhMeasurementEngine.Measure(c.BuildSurface(), MeasurementMethod.SlopeContour);
        }

        private static OpticNerveHeadSpec Advanced()
        {
            OpticNerveHeadSpec s = OpticNerveHeadSpec.Healthy();
            s.CupToDiscVertical = 0.9f;
            s.CupToDiscHorizontal = 0.88f;
            s.CupDepthMm = 0.85f;
            s.RimScaleInferior = 0.35f;
            s.RimScaleSuperior = 0.40f;
            return s;
        }
    }
}
