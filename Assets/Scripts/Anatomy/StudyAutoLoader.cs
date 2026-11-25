// -----------------------------------------------------------------------------
// Ocular -- loads the patient the pipeline just imported.
//
// Tools/ocular_pipeline.py writes a FundusStudy into Resources/FundusStudies.
// This finds it at startup and hands it to the model binding, so the running
// simulator opens on the imported eye instead of on a teaching preset.
//
// Resources, again, rather than a scene reference: the scene is generated from
// code, so a reference filled in at generation time would go stale the moment
// the next patient is imported, and the pipeline would have to regenerate and
// rebuild the scene for every single image. This way the import step touches
// only assets.
//
// Finding nothing is a normal outcome, not an error. With no study present the
// simulator opens on the case library exactly as it always has, which is what
// should happen on a fresh checkout and after the studies are cleared out.
// -----------------------------------------------------------------------------

using UnityEngine;
using Ocular.Core.Diagnostics;
using Ocular.Core.Imaging;
using Ocular.Data;

namespace Ocular.Anatomy
{
    [RequireComponent(typeof(OnhModelBinding))]
    public sealed class StudyAutoLoader : MonoBehaviour
    {
        /// <summary>Where the pipeline writes imported studies.</summary>
        public const string ResourceFolder = "FundusStudies";

        [Tooltip("Load an imported study at startup. Turn off to always open on the case library.")]
        [SerializeField] private bool _loadOnStart = true;

        /// <summary>The fit of the study that was loaded, or null if none was.</summary>
        public OnhFitReport Report { get; private set; }

        /// <summary>
        /// Stands down, so something else may own the eye on screen.
        ///
        /// <para>Called from EyeImportController.Awake when the app was launched to
        /// import a specific image. Both components load in Start and Unity does
        /// not order Start between components, so whichever ran second won -- and
        /// when that was this one, the clinician's chosen image was silently
        /// replaced by whichever study happened to be baked into the build. The
        /// handover is done in Awake precisely because every Awake runs before any
        /// Start, which makes the outcome deterministic instead of a coin toss.</para>
        /// </summary>
        public void SuppressAutoLoad()
        {
            _loadOnStart = false;
        }

        private void Start()
        {
            if (!_loadOnStart) return;

            FundusStudy study = FindNewestStudy();
            if (study == null) return;

            OnhModelBinding binding = GetComponent<OnhModelBinding>();
            Report = binding.LoadStudy(study);

            if (Report == null) return;

            // Surfaced at Info rather than swallowed: when an imported eye opens
            // with an assumed depth or an assumed scale, that belongs in the log
            // next to the numbers it qualifies, not only in a report object.
            OLog.Info(LogCat.Anatomy, "Auto-loaded study '" + study.DisplayName
                + "' -- " + Report.Summary());

            for (int i = 0; i < Report.Caveats.Count; i++)
                OLog.Warn(LogCat.Anatomy, Report.Caveats[i]);
        }

        /// <summary>
        /// The study to open on. Alphabetically last, so a slug carrying a date
        /// sorts to the newest; with a single study the question never arises.
        /// </summary>
        private static FundusStudy FindNewestStudy()
        {
            FundusStudy[] studies = Resources.LoadAll<FundusStudy>(ResourceFolder);

            if (studies == null || studies.Length == 0) return null;

            FundusStudy best = studies[0];

            for (int i = 1; i < studies.Length; i++)
                if (string.CompareOrdinal(studies[i].name, best.name) > 0) best = studies[i];

            if (studies.Length > 1)
            {
                OLog.Info(LogCat.Anatomy, studies.Length + " studies installed; opening '"
                    + best.DisplayName + "'. Press N to step through the teaching cases.");
            }

            return best;
        }
    }
}
