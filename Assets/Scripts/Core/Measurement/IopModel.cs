// -----------------------------------------------------------------------------
// Ocular -- intraocular pressure, and why it is not the diagnosis.
//
// IOP is a RISK FACTOR for glaucoma, not a definition of it. Two of the case
// presets exist purely to break the association a learner tends to form:
// ocular hypertension (high pressure, healthy nerve) and normal-tension
// glaucoma (normal pressure, damaged nerve). Nothing in this file decides
// whether an eye has glaucoma, and nothing that consumes it should either.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace Ocular.Core.Measurement
{
    public enum IopBand
    {
        Low,
        Normal,
        Ocular_Hypertension,
        Markedly_Elevated
    }

    public static class IopModel
    {
        /// <summary>Central corneal thickness the Goldmann tonometer is calibrated around, microns.</summary>
        public const float ReferenceCctUm = 545f;

        /// <summary>Conventional teaching correction: 0.5 mmHg per 10 microns of CCT.</summary>
        public const float MmHgPerMicron = 0.05f;

        public const float NormalLowMmHg = 10f;
        public const float NormalHighMmHg = 21f;

        /// <summary>
        /// Applanation IOP adjusted for central corneal thickness.
        ///
        /// <para>DIRECTION MATTERS AND IS EASY TO INVERT. Goldmann applanation has to
        /// flatten the cornea; a THICK cornea resists more, so the tonometer reads
        /// HIGHER than the true pressure and the correction must bring it DOWN. A
        /// thin cornea does the reverse, which is why a thin-corneaed patient with a
        /// "normal" 18 mmHg can be under-treated -- the Ocular Hypertension Treatment
        /// Study found thin corneas to be a strong independent risk factor.</para>
        ///
        /// <para>Every published linear correction disagrees with every other one and
        /// none of them is validated for clinical decisions. This is the teaching
        /// form, and the UI labels it as such.</para>
        /// </summary>
        public static float CorrectedMmHg(float measuredMmHg, float cctUm)
        {
            return measuredMmHg - (cctUm - ReferenceCctUm) * MmHgPerMicron;
        }

        /// <summary>How much the cornea is distorting the reading, mmHg (signed).</summary>
        public static float CorrectionMmHg(float cctUm)
        {
            return -(cctUm - ReferenceCctUm) * MmHgPerMicron;
        }

        public static IopBand Classify(float mmHg)
        {
            if (mmHg < NormalLowMmHg) return IopBand.Low;
            if (mmHg <= NormalHighMmHg) return IopBand.Normal;
            if (mmHg <= 30f) return IopBand.Ocular_Hypertension;
            return IopBand.Markedly_Elevated;
        }

        public static string Describe(float measuredMmHg, float cctUm)
        {
            float corrected = CorrectedMmHg(measuredMmHg, cctUm);
            float delta = CorrectionMmHg(cctUm);

            string cornea = Mathf.Abs(delta) < 0.5f
                ? "cornea near reference thickness"
                : (delta < 0f
                    ? "thick cornea, reading overestimates true pressure"
                    : "thin cornea, reading underestimates true pressure");

            return measuredMmHg.ToString("0.0") + " mmHg measured, "
                 + corrected.ToString("0.0") + " mmHg CCT-corrected ("
                 + cctUm.ToString("0") + " um: " + cornea + ")";
        }
    }
}
