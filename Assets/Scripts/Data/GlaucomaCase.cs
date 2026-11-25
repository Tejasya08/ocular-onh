// -----------------------------------------------------------------------------
// Ocular -- one teaching case.
//
// ONE ScriptableObject type in this file, and the file is named after it. Unity
// binds a .asset to its script by file identity; a second SO type declared here
// would get a null script binding and load as an empty object with no error, and
// the case would silently appear blank in the selector.
//
// A case is a nerve head plus the clinical context needed to read it. The
// context is not decoration: half these cases cannot be interpreted correctly
// from the disc alone, which is the point.
// -----------------------------------------------------------------------------

using UnityEngine;
using Ocular.Core.Anatomy;

namespace Ocular.Data
{
    [CreateAssetMenu(fileName = "GlaucomaCase", menuName = "Ocular/Glaucoma Case")]
    public sealed class GlaucomaCase : ScriptableObject
    {
        [Header("Identity")]
        public string DisplayName = "Untitled case";

        [Tooltip("Which eye. Presentation only - the geometry is authored in disc-space.")]
        public Laterality Laterality = Laterality.OD;

        [Range(5, 95)]
        public int PatientAge = 60;

        [Header("Optic nerve head")]
        public OpticNerveHeadSpec Onh = OpticNerveHeadSpec.Healthy();

        [Header("Clinical context")]
        [Tooltip("Goldmann applanation IOP, mmHg. A risk factor, never the diagnosis.")]
        public float IopMmHg = 15f;

        [Tooltip("Central corneal thickness, microns. Reference is 545.")]
        public float CentralCornealThicknessUm = 545f;

        [Header("Teaching")]
        [TextArea(3, 8)]
        public string Vignette = string.Empty;

        [TextArea(3, 8)]
        [Tooltip("What this case is here to teach. Shown after the learner commits an estimate.")]
        public string TeachingNote = string.Empty;

        /// <summary>Builds the evaluable surface for this case.</summary>
        public OnhSurface BuildSurface()
        {
            return new OnhSurface(Onh);
        }
    }
}
