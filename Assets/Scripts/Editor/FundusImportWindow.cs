// -----------------------------------------------------------------------------
// Ocular -- the import workflow: photograph in, GlaucomaCase out.
//
//   1. Point at a fundus photograph and say which eye it is.
//   2. Calibrate. This is the step people skip and it is the one that decides
//      whether disc size means anything.
//   3. Auto-trace the disc and cup, then CORRECT the cup by hand -- the auto
//      cup follows pallor, and pallor is not cupping.
//   4. Supply an OCT depth if there is one. Without it the cup depth is a
//      population figure and is labelled as such everywhere it appears.
//   5. Fit, inspect the residual, write the case.
//
// The window deliberately shows the residual and the provenance flags rather
// than just a tick. A fit that silently succeeded is how a mis-centred trace
// turns into a confident CDR.
// -----------------------------------------------------------------------------

using UnityEditor;
using UnityEngine;
using Ocular.Anatomy;
using Ocular.Core.Anatomy;
using Ocular.Core.Imaging;
using Ocular.Data;

namespace Ocular.Editor
{
    public sealed class FundusImportWindow : EditorWindow
    {
        private FundusStudy _study;
        private OnhFitReport _report;
        private SegmentationResult _segmentation;
        private string _status = string.Empty;
        private Vector2 _scroll;

        [MenuItem("Ocular/Import Fundus Photograph")]
        public static void Open()
        {
            FundusImportWindow window = GetWindow<FundusImportWindow>(true, "Import Fundus Photograph");
            window.minSize = new Vector2(460f, 560f);
            window.Show();
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.HelpBox(
                "Builds a nerve head from a real photograph. The fitted spec still goes through "
                + "OnhSurface and the measurement engine, so the numbers on screen stay "
                + "measurements of geometry rather than of this trace.",
                MessageType.None);

            _study = (FundusStudy)EditorGUILayout.ObjectField("Study", _study, typeof(FundusStudy), false);

            if (GUILayout.Button("New study asset...")) CreateStudy();

            if (_study == null)
            {
                EditorGUILayout.HelpBox("Create or select a FundusStudy to begin.", MessageType.Info);
                EditorGUILayout.EndScrollView();
                return;
            }

            EditorGUILayout.Space();
            DrawStudyFields();

            EditorGUILayout.Space();
            DrawActions();

            EditorGUILayout.Space();
            DrawReport();

            if (!string.IsNullOrEmpty(_status)) EditorGUILayout.HelpBox(_status, MessageType.Info);

            EditorGUILayout.EndScrollView();
        }

        private void DrawStudyFields()
        {
            UnityEditor.Editor inspector = UnityEditor.Editor.CreateEditor(_study);
            inspector.OnInspectorGUI();
            DestroyImmediate(inspector);
        }

        private void DrawActions()
        {
            using (new EditorGUI.DisabledScope(_study.Photograph == null))
            {
                if (GUILayout.Button("1. Make photograph readable")) MakeReadable();
                if (GUILayout.Button("2. Auto-trace disc and cup")) AutoTrace();
            }

            using (new EditorGUI.DisabledScope(!_study.IsTraced))
            {
                if (GUILayout.Button("3. Fit spec from trace")) Fit();
                if (GUILayout.Button("4. Write GlaucomaCase asset...")) WriteCase();
            }
        }

        private void DrawReport()
        {
            if (_report == null) return;

            EditorGUILayout.LabelField("Fit", EditorStyles.boldLabel);

            EditorGUILayout.LabelField("Disc RMS", _report.DiscResidualRmsMm.ToString("0.0000") + " mm");
            EditorGUILayout.LabelField("Cup RMS", _report.CupResidualRmsMm.ToString("0.0000") + " mm");
            EditorGUILayout.LabelField("Worst", _report.MaxResidualMm.ToString("0.0000") + " mm");

            OpticNerveHeadSpec s = _report.Spec;

            EditorGUILayout.LabelField("Disc V x H",
                s.DiscDiameterVerticalMm.ToString("0.00") + " x "
                + s.DiscDiameterHorizontalMm.ToString("0.00") + " mm"
                + (_report.DiscDiameterIsMeasured ? string.Empty : "   (ASSUMED)"));

            EditorGUILayout.LabelField("CDR V / H",
                s.CupToDiscVertical.ToString("0.00") + " / " + s.CupToDiscHorizontal.ToString("0.00"));

            EditorGUILayout.LabelField("Cup depth",
                s.CupDepthMm.ToString("0.00") + " mm   (" + _report.DepthLabel() + ")");

            EditorGUILayout.LabelField("Rim I/S/N/T",
                s.RimScaleInferior.ToString("0.00") + " / " + s.RimScaleSuperior.ToString("0.00")
                + " / " + s.RimScaleNasal.ToString("0.00") + " / " + s.RimScaleTemporal.ToString("0.00"));

            EditorGUILayout.LabelField("Notch", _report.NotchDetected
                ? s.NotchAngleDeg.ToString("0") + " deg, depth " + s.NotchDepth.ToString("0.00")
                  + ", sigma " + s.NotchWidthDeg.ToString("0") + " deg"
                : "none");

            if (!_report.IsGoodFit)
            {
                EditorGUILayout.HelpBox("The rebuilt surface does not follow the trace. Check the disc "
                                      + "centre and re-trace.", MessageType.Warning);
            }

            for (int i = 0; i < _report.Caveats.Count; i++)
                EditorGUILayout.HelpBox(_report.Caveats[i], MessageType.Warning);
        }

        private void CreateStudy()
        {
            string path = EditorUtility.SaveFilePanelInProject(
                "New fundus study", "FundusStudy", "asset", "Where should the study live?");

            if (string.IsNullOrEmpty(path)) return;

            FundusStudy study = CreateInstance<FundusStudy>();
            AssetDatabase.CreateAsset(study, path);
            AssetDatabase.SaveAssets();

            _study = study;
            _report = null;
            _status = "Created " + path;
        }

        /// <summary>
        /// Turns on Read/Write for the photograph.
        ///
        /// <para>Off by default on every imported texture, and the failure it causes
        /// is opaque: GetPixels throws deep inside the projection and the disc comes
        /// out with procedural tissue on it, looking like the import silently did
        /// nothing rather than like a texture setting.</para>
        /// </summary>
        private void MakeReadable()
        {
            string path = AssetDatabase.GetAssetPath(_study.Photograph);
            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;

            if (importer == null)
            {
                _status = "That texture has no importer -- is it generated rather than a file?";
                return;
            }

            importer.isReadable = true;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.mipmapEnabled = false;

            // A compressed photograph fits blocky margins. The disc margin is a
            // few pixels of gradient and DXT quantises it into steps that the
            // radial threshold then follows.
            importer.textureCompression = TextureImporterCompression.Uncompressed;

            importer.SaveAndReimport();

            _status = "Read/Write enabled on " + path;
        }

        private void AutoTrace()
        {
            FundusImage image = FundusPhotoTexture.ToFundusImage(_study.Photograph);

            if (image == null)
            {
                _status = "Could not read the photograph. Run step 1 first.";
                return;
            }

            ImageCalibration calibration = _study.BuildCalibration();

            // A disc is never wider than about 3.2 mm, so the search radius follows
            // from the calibration rather than from a pixel guess that would have to
            // change with every camera.
            float searchRadiusPx = calibration.MmToPixels(2.2f);

            Vector2 seed = _study.DiscCentrePixels;

            if (seed == Vector2.zero)
            {
                seed = DiscSegmenter.FindDiscSeed(image, Mathf.Max(4, Mathf.RoundToInt(searchRadiusPx * 0.5f)));
            }

            _segmentation = DiscSegmenter.Segment(image, seed, searchRadiusPx,
                                                  calibration, _study.Laterality);

            Undo.RecordObject(_study, "Auto-trace fundus");

            _study.DiscCentrePixels = _segmentation.CentrePixels;
            _study.DiscMargin = _segmentation.Disc.ToPolygonPixels(calibration, _study.Laterality);
            _study.CupMargin = _segmentation.Cup.ToPolygonPixels(calibration, _study.Laterality);

            EditorUtility.SetDirty(_study);
            AssetDatabase.SaveAssets();

            _status = _segmentation.CupIsReliable
                ? "Traced. Review the CUP margin against the vessel bends before fitting -- the "
                  + "auto-trace follows pallor, which is not the same boundary."
                : _segmentation.Note;
        }

        private void Fit()
        {
            _report = _study.Fit();
            _status = _report == null ? "Nothing traced yet." : _report.Summary();
        }

        private void WriteCase()
        {
            if (_report == null) Fit();
            if (_report == null) return;

            string path = EditorUtility.SaveFilePanelInProject(
                "Write case", _study.DisplayName.Replace(' ', '_'), "asset",
                "Where should the fitted case live?");

            if (string.IsNullOrEmpty(path)) return;

            GlaucomaCase existing = AssetDatabase.LoadAssetAtPath<GlaucomaCase>(path);
            GlaucomaCase target = existing != null ? existing : CreateInstance<GlaucomaCase>();

            _study.ApplyTo(target, _report);

            if (existing == null) AssetDatabase.CreateAsset(target, path);

            EditorUtility.SetDirty(target);
            AssetDatabase.SaveAssets();

            _status = "Wrote " + path + ". Re-run Ocular > Generate Simulation Scene to pick it up "
                    + "-- a clean compile does not rebuild the scene.";
        }
    }
}
