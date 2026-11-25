// -----------------------------------------------------------------------------
// Ocular -- the built-in teaching cases.
//
// Every case starts from OpticNerveHeadSpec.Healthy() and changes only what makes
// it abnormal, so the diff between a case and normal IS the pathology. Reading
// one of these builders should tell you what the case is about.
//
// The set is chosen so that CDR alone cannot get a learner through it. Two cases
// exist specifically to be false positives -- a large physiologic cup and ocular
// hypertension -- and one is a false negative, normal-tension glaucoma. A case
// list of nothing but obvious glaucoma teaches a learner to over-call every big
// cup they ever see, which is the error that actually happens in clinic.
//
// These are fictional teaching vignettes, not records of real patients.
// -----------------------------------------------------------------------------

using UnityEngine;
using Ocular.Core.Anatomy;

namespace Ocular.Data
{
    public static class CasePresets
    {
        /// <summary>Builds every built-in case. Caller owns the returned objects.</summary>
        public static GlaucomaCase[] BuildAll()
        {
            return new[]
            {
                Healthy(),
                LargePhysiologicCup(),
                OcularHypertension(),
                EarlyPoag(),
                NormalTensionGlaucoma(),
                ModeratePoag(),
                AdvancedPoag()
            };
        }

        private static GlaucomaCase New(string assetName, string display)
        {
            GlaucomaCase c = ScriptableObject.CreateInstance<GlaucomaCase>();
            c.name = assetName;
            c.DisplayName = display;
            c.Onh = OpticNerveHeadSpec.Healthy();
            return c;
        }

        // =====================================================================

        public static GlaucomaCase Healthy()
        {
            GlaucomaCase c = New("Case1_Healthy", "1. Healthy average disc");
            c.Laterality = Laterality.OD;
            c.PatientAge = 55;
            c.IopMmHg = 15f;
            c.CentralCornealThicknessUm = 545f;

            c.Vignette =
                "55-year-old attending for routine review. No visual symptoms, no family "
                + "history of glaucoma. Pressure 15 mmHg, cornea of average thickness.";

            c.TeachingNote =
                "The reference point for everything else. Average disc, cup-to-disc ratio "
                + "about 0.3, and a rim that obeys ISNT: widest inferiorly, then superiorly, "
                + "then nasally, thinnest temporally. Note that the rim is NOT uniform in a "
                + "healthy eye - learners often expect symmetry and then call a normal "
                + "thin temporal rim abnormal.";

            return c;
        }

        public static GlaucomaCase LargePhysiologicCup()
        {
            GlaucomaCase c = New("Case2_LargePhysiologicCup", "2. Large physiologic cup");
            c.Laterality = Laterality.OS;
            c.PatientAge = 42;
            c.IopMmHg = 16f;
            c.CentralCornealThicknessUm = 550f;

            // A big disc carries a big cup on a completely normal amount of rim.
            c.Onh.DiscDiameterVerticalMm = 2.25f;
            c.Onh.DiscDiameterHorizontalMm = 2.20f;
            c.Onh.CupToDiscVertical = 0.62f;
            c.Onh.CupToDiscHorizontal = 0.65f;
            c.Onh.CupDepthMm = 0.38f;
            // Rim scales left at healthy: ISNT is intact, which is the whole point.

            c.Vignette =
                "42-year-old referred by an optometrist for a 'suspicious cup-to-disc ratio "
                + "of 0.65'. Asymptomatic. Pressure 16 mmHg. No family history.";

            c.TeachingNote =
                "THE CLASSIC FALSE POSITIVE. The CDR really is 0.65, and this eye is normal. "
                + "Check the disc size before you read the ratio: this is a large disc, so a "
                + "large cup sits on a normal amount of neuroretinal tissue. Look at the rim "
                + "AREA rather than the ratio, and check ISNT - both are normal here. This is "
                + "also why the DDLS shifts its stage by disc size, and why a bare CDR is a "
                + "poor screening number.";

            return c;
        }

        public static GlaucomaCase OcularHypertension()
        {
            GlaucomaCase c = New("Case3_OcularHypertension", "3. Ocular hypertension");
            c.Laterality = Laterality.OD;
            c.PatientAge = 58;
            c.IopMmHg = 28f;
            c.CentralCornealThicknessUm = 610f;

            c.Onh.CupToDiscVertical = 0.35f;
            c.Onh.CupToDiscHorizontal = 0.36f;

            c.Vignette =
                "58-year-old found to have pressures of 28 mmHg in both eyes. Discs look "
                + "healthy. Central corneal thickness 610 microns. Fields full.";

            c.TeachingNote =
                "High pressure, healthy nerve - this is ocular hypertension, not glaucoma. "
                + "Two things to take away. First, IOP is a RISK FACTOR, not the diagnosis; "
                + "the diagnosis lives at the optic nerve and the visual field. Second, this "
                + "cornea is thick, so the tonometer reads higher than the true pressure and "
                + "the corrected figure is meaningfully lower. The Ocular Hypertension "
                + "Treatment Study found corneal thickness to be a strong independent "
                + "predictor of who actually converts to glaucoma.";

            return c;
        }

        public static GlaucomaCase EarlyPoag()
        {
            GlaucomaCase c = New("Case4_EarlyPOAG", "4. Early primary open-angle glaucoma");
            c.Laterality = Laterality.OD;
            c.PatientAge = 66;
            c.IopMmHg = 24f;
            c.CentralCornealThicknessUm = 540f;

            c.Onh.CupToDiscVertical = 0.60f;
            c.Onh.CupToDiscHorizontal = 0.55f;
            c.Onh.CupDepthMm = 0.42f;
            c.Onh.CupFloorFlatness = 3.0f;

            // Inferior rim thinned below superior: ISNT breaks at its first
            // inequality, which is where glaucoma usually breaks it.
            c.Onh.RimScaleInferior = 0.62f;

            c.Vignette =
                "66-year-old with pressures in the mid-20s. No symptoms. The vertical "
                + "cup-to-disc ratio is 0.6 and the inferior rim looks thinner than the "
                + "superior rim.";

            c.TeachingNote =
                "The CDR here is only modestly raised, but the rim ordering is wrong: the "
                + "inferior rim has become thinner than the superior. ISNT is broken, and it "
                + "is broken at the inferior pole, which is where glaucoma characteristically "
                + "starts. The pattern of rim loss matters more than the size of the cup - a "
                + "0.6 cup with an intact ISNT ordering is far less worrying than this.";

            return c;
        }

        public static GlaucomaCase NormalTensionGlaucoma()
        {
            GlaucomaCase c = New("Case5_NormalTension", "5. Normal-tension glaucoma with notch");
            c.Laterality = Laterality.OS;
            c.PatientAge = 71;
            c.IopMmHg = 15f;
            c.CentralCornealThicknessUm = 505f;

            c.Onh.CupToDiscVertical = 0.70f;
            c.Onh.CupToDiscHorizontal = 0.60f;
            c.Onh.CupDepthMm = 0.50f;
            c.Onh.CupFloorFlatness = 3.5f;
            c.Onh.RimScaleInferior = 0.75f;

            // Focal inferior notch, with the splinter haemorrhage that so often sits
            // at its edge.
            c.Onh.NotchAngleDeg = 270f;
            c.Onh.NotchDepth = 0.75f;
            c.Onh.NotchWidthDeg = 16f;
            c.Onh.HasDiscHaemorrhage = true;
            c.Onh.HaemorrhageAngleDeg = 268f;

            c.Vignette =
                "71-year-old, pressures consistently 14-16 mmHg. Thin cornea at 505 microns. "
                + "A splinter haemorrhage is visible at the inferior disc margin. Reports "
                + "nothing wrong with the vision.";

            c.TeachingNote =
                "THE FALSE NEGATIVE. Pressure is normal and this eye still has glaucoma. Note "
                + "the focal inferior notch rather than diffuse thinning, and the disc "
                + "haemorrhage - a Drance haemorrhage is a marker of ongoing damage and "
                + "predicts progression. The thin cornea means the true pressure is higher "
                + "than 15. Because the notch is INFERIOR, look for the field defect in the "
                + "SUPERIOR field: the optics invert, so inferior rim serves superior field.";

            return c;
        }

        public static GlaucomaCase ModeratePoag()
        {
            GlaucomaCase c = New("Case6_ModeratePOAG", "6. Moderate primary open-angle glaucoma");
            c.Laterality = Laterality.OD;
            c.PatientAge = 70;
            c.IopMmHg = 26f;
            c.CentralCornealThicknessUm = 535f;

            c.Onh.CupToDiscVertical = 0.80f;
            c.Onh.CupToDiscHorizontal = 0.72f;
            c.Onh.CupDepthMm = 0.60f;
            c.Onh.CupFloorFlatness = 4.0f;

            // Both vertical poles going, which is the usual progression.
            c.Onh.RimScaleInferior = 0.50f;
            c.Onh.RimScaleSuperior = 0.55f;
            c.Onh.RimScaleNasal = 0.90f;
            c.Onh.RimScaleTemporal = 0.70f;

            c.Onh.PpaWidthMm = 0.45f;
            c.Onh.PpaAngleDeg = 0f;
            c.Onh.PpaArcDeg = 150f;

            c.Vignette =
                "70-year-old on treatment, pressures still 26 mmHg. Cup-to-disc 0.8 with rim "
                + "loss at both vertical poles and a crescent of peripapillary atrophy.";

            c.TeachingNote =
                "Established damage at both poles. The rim is now thinnest where it should be "
                + "thickest, and beta-zone peripapillary atrophy has appeared - atrophy that "
                + "enlarges over time tracks with progression. Use the cross-section view "
                + "here: cup DEPTH has increased along with its width, which a flat fundus "
                + "photograph cannot show you at all.";

            return c;
        }

        public static GlaucomaCase AdvancedPoag()
        {
            GlaucomaCase c = New("Case7_AdvancedPOAG", "7. Advanced glaucoma, bean-pot cupping");
            c.Laterality = Laterality.OS;
            c.PatientAge = 76;
            c.IopMmHg = 30f;
            c.CentralCornealThicknessUm = 530f;

            c.Onh.CupToDiscVertical = 0.90f;
            c.Onh.CupToDiscHorizontal = 0.88f;

            // Deep floor with steep walls: the "bean-pot" cup, undermined at its edges.
            c.Onh.CupDepthMm = 0.85f;
            c.Onh.CupFloorFlatness = 6.0f;

            c.Onh.RimScaleInferior = 0.35f;
            c.Onh.RimScaleSuperior = 0.40f;
            c.Onh.RimScaleNasal = 0.80f;
            c.Onh.RimScaleTemporal = 0.60f;

            c.Onh.PpaWidthMm = 0.70f;
            c.Onh.PpaAngleDeg = 0f;
            c.Onh.PpaArcDeg = 260f;

            c.Vignette =
                "76-year-old presenting late with pressures of 30 mmHg and tunnel vision. "
                + "Only a thin nasal rim remains.";

            c.TeachingNote =
                "End-stage cupping. The rim survives longest nasally, which is why the last "
                + "island of vision is usually temporal. Two limits of measurement show "
                + "themselves here: RNFL thickness has hit its floor, so OCT can no longer "
                + "track further damage, and the visual field becomes the only instrument "
                + "still able to detect progression. This is also why late presentation is "
                + "so costly - none of this is recoverable.";

            return c;
        }
    }
}
