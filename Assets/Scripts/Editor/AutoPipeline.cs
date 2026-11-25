// -----------------------------------------------------------------------------
// Ocular -- the Unity half of the one-image pipeline.
//
// Tools/ocular_pipeline.py drops a job file and calls RunBatch. This imports
// the photograph, traces the disc and cup, writes a FundusStudy into Resources
// and leaves a human-readable fit report beside it.
//
// The study goes into Resources rather than into the CaseLibrary on purpose.
// OcularSceneGenerator.BuildCaseLibrary CLEARS the library and rebuilds it from
// CasePresets every time it runs, so a fitted patient case added there would
// survive exactly until the next scene regeneration and then vanish with no
// error. Resources is outside that blast radius, needs no scene regeneration to
// pick up a new study, and works identically in a player build.
//
// Nothing here measures anything. It produces traced contours; the fit happens
// in FundusStudy.Fit, the surface is generated from the fitted spec, and the
// measurement engine reads that surface at run time. The report written here is
// a convenience copy of OnhFitReport.Summary, not a separate source of numbers.
// -----------------------------------------------------------------------------

using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using Ocular.Anatomy;
using Ocular.Core.Anatomy;
using Ocular.Core.Diagnostics;
using Ocular.Core.Imaging;
using Ocular.Data;

namespace Ocular.Editor
{
    public static class AutoPipeline
    {
        private const string StudyDir = "Assets/Resources/FundusStudies";

        /// <summary>
        /// The job file, at the project root rather than under Assets.
        ///
        /// <para>Anywhere under Resources it would be imported as a TextAsset and
        /// shipped inside the player, carrying the patient's name and clinical
        /// context into a distributable build. Anchored to Application.dataPath
        /// because batch mode does not promise a working directory.</para>
        /// </summary>
        private static string JobPath
        {
            get { return Path.GetFullPath(Path.Combine(Application.dataPath, "../pipeline_job.json")); }
        }

        [Serializable]
        private sealed class Job
        {
            public string displayName;
            public string slug;
            public string laterality;
            public int patientAge;
            public float iopMmHg;
            public float centralCornealThicknessUm;
            public float fieldAngleDeg;
            public float axialLengthMm;
            public string photoResourcePath;
            public string photoAssetPath;
            public float cupDepthMm;
        }

        /// <summary>CI entry point: -executeMethod Ocular.Editor.AutoPipeline.RunBatch</summary>
        public static void RunBatch()
        {
            int exitCode = 0;

            try
            {
                Run();
            }
            catch (Exception ex)
            {
                OLog.Error(LogCat.Build, "Pipeline threw: " + ex);
                exitCode = 1;
            }

            if (Application.isBatchMode) EditorApplication.Exit(exitCode);
        }

        public static void Run()
        {
            AssetDatabase.Refresh();

            if (!File.Exists(JobPath))
                throw new FileNotFoundException("No pipeline job at " + JobPath);

            Job job = JsonUtility.FromJson<Job>(File.ReadAllText(JobPath));

            if (job == null || string.IsNullOrEmpty(job.slug))
                throw new InvalidDataException("Pipeline job is empty or has no slug.");

            Texture2D photo = ImportPhotograph(job.photoAssetPath);

            FundusImage image = FundusPhotoTexture.ToFundusImage(photo);
            if (image == null)
                throw new InvalidOperationException(
                    "Could not read " + job.photoAssetPath + " even after enabling Read/Write.");

            Laterality laterality = job.laterality == "OS" ? Laterality.OS : Laterality.OD;

            // Field width from the fundus circle, not the file. A photograph
            // letterboxed into a wider frame is scaled wrong by exactly the ratio
            // between the two, and that error goes straight into every reported
            // millimetre.
            float fieldPixels = image.EstimateFieldWidthPixels();

            ImageCalibration calibration = ImageCalibration.FromCameraField(
                job.fieldAngleDeg, fieldPixels, job.axialLengthMm);

            float searchRadiusPx = calibration.MmToPixels(2.2f);

            Vector2 seed = DiscSegmenter.FindDiscSeed(
                image, Mathf.Max(4, Mathf.RoundToInt(searchRadiusPx * 0.5f)));

            SegmentationResult segmentation = DiscSegmenter.Segment(
                image, seed, searchRadiusPx, calibration, laterality);

            FundusStudy study = CreateOrUpdateStudy(job, photo, laterality,
                                                    fieldPixels, segmentation, calibration);

            OnhFitReport report = study.Fit();

            WriteReport(job, study, segmentation, calibration, report, fieldPixels);

            EditorUtility.SetDirty(study);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            OLog.Info(LogCat.Build, "Pipeline wrote " + StudyDir + "/" + job.slug + ".asset"
                + (report != null ? " -- " + report.Summary() : string.Empty));
        }

        /// <summary>
        /// Forces the import settings the imaging code needs.
        ///
        /// <para>Read/Write is off by default on every imported texture and
        /// GetPixels throws without it. Compression is the subtler one: the disc
        /// margin is a few pixels of gradient, and DXT quantises that into blocky
        /// steps which the radial threshold then faithfully traces, producing a
        /// margin with a stair pattern in it that looks like real contour
        /// irregularity.</para>
        /// </summary>
        private static Texture2D ImportPhotograph(string assetPath)
        {
            TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;

            if (importer == null)
                throw new FileNotFoundException("No texture importer for " + assetPath);

            importer.textureType = TextureImporterType.Default;
            importer.isReadable = true;
            importer.mipmapEnabled = false;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.maxTextureSize = 4096;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;

            importer.SaveAndReimport();

            Texture2D photo = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);

            if (photo == null)
                throw new FileNotFoundException("Could not load texture at " + assetPath);

            return photo;
        }

        private static FundusStudy CreateOrUpdateStudy(Job job, Texture2D photo,
                                                       Laterality laterality,
                                                       float fieldPixels,
                                                       SegmentationResult segmentation,
                                                       ImageCalibration calibration)
        {
            if (!AssetDatabase.IsValidFolder("Assets/Resources"))
                AssetDatabase.CreateFolder("Assets", "Resources");

            if (!AssetDatabase.IsValidFolder(StudyDir))
                AssetDatabase.CreateFolder("Assets/Resources", "FundusStudies");

            string path = StudyDir + "/" + job.slug + ".asset";

            FundusStudy study = AssetDatabase.LoadAssetAtPath<FundusStudy>(path);
            bool isNew = study == null;

            if (isNew) study = ScriptableObject.CreateInstance<FundusStudy>();

            study.DisplayName = job.displayName;
            study.Laterality = laterality;
            study.PatientAge = Mathf.Clamp(job.patientAge, 5, 95);
            study.Photograph = photo;

            study.ScaleSource = ScaleSource.CameraField;
            study.FieldAngleDeg = job.fieldAngleDeg;
            study.FieldPixels = fieldPixels;
            study.AxialLengthMm = job.axialLengthMm;

            study.DiscCentrePixels = segmentation.CentrePixels;
            study.DiscMargin = segmentation.Disc.ToPolygonPixels(calibration, laterality);
            study.CupMargin = segmentation.Cup.ToPolygonPixels(calibration, laterality);

            study.IopMmHg = job.iopMmHg;
            study.CentralCornealThicknessUm = job.centralCornealThicknessUm;

            // A negative depth is the job file's way of saying "no OCT". Anything
            // else would be indistinguishable from a real 0 mm reading.
            if (job.cupDepthMm > 0f)
            {
                study.DepthProvenance = DepthProvenance.OctMeasured;
                study.CupDepthMm = job.cupDepthMm;
            }
            else
            {
                study.DepthProvenance = DepthProvenance.PopulationAssumed;
            }

            study.Vignette = "Imported automatically from a fundus photograph on "
                           + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + ".";

            if (isNew) AssetDatabase.CreateAsset(study, path);

            return study;
        }

        private static void WriteReport(Job job, FundusStudy study,
                                        SegmentationResult segmentation,
                                        ImageCalibration calibration,
                                        OnhFitReport report, float fieldPixels)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(1400);

            sb.AppendLine("OCULAR -- " + job.displayName + " (" + study.Laterality + ")");
            sb.AppendLine(new string('-', 64));
            sb.AppendLine("Calibration   " + calibration.Describe());
            sb.AppendLine("Fundus circle " + fieldPixels.ToString("0") + " px wide");
            sb.AppendLine("Disc centre   " + segmentation.CentrePixels.x.ToString("0") + ", "
                                           + segmentation.CentrePixels.y.ToString("0") + " px");
            sb.AppendLine();

            if (report == null)
            {
                sb.AppendLine("NO FIT -- the trace did not produce usable contours.");
            }
            else
            {
                OpticNerveHeadSpec s = report.Spec;

                sb.AppendLine("Disc      " + s.DiscDiameterVerticalMm.ToString("0.00") + " x "
                              + s.DiscDiameterHorizontalMm.ToString("0.00") + " mm"
                              + (report.DiscDiameterIsMeasured ? string.Empty : "   (ASSUMED)"));
                sb.AppendLine("CDR       V " + s.CupToDiscVertical.ToString("0.00")
                              + "   H " + s.CupToDiscHorizontal.ToString("0.00"));
                sb.AppendLine("Cup depth " + s.CupDepthMm.ToString("0.00") + " mm   ("
                              + report.DepthLabel() + ")");
                sb.AppendLine("Rim       I " + s.RimScaleInferior.ToString("0.00")
                              + "  S " + s.RimScaleSuperior.ToString("0.00")
                              + "  N " + s.RimScaleNasal.ToString("0.00")
                              + "  T " + s.RimScaleTemporal.ToString("0.00"));
                sb.AppendLine("Notch     " + (report.NotchDetected
                              ? s.NotchAngleDeg.ToString("0") + " deg, depth "
                                + s.NotchDepth.ToString("0.00")
                              : "none"));
                sb.AppendLine("Fit       " + report.Summary());
                sb.AppendLine();

                if (!segmentation.CupIsReliable)
                {
                    sb.AppendLine("WARNING   " + segmentation.Note);
                    sb.AppendLine();
                }

                for (int i = 0; i < report.Caveats.Count; i++)
                {
                    sb.AppendLine("CAVEAT    " + report.Caveats[i]);
                    sb.AppendLine();
                }
            }

            sb.AppendLine("The eye globe shown around the disc is GENERATED SCENERY. No number");
            sb.AppendLine("above comes from it; all of them are measured off the fitted surface.");

            File.WriteAllText(StudyDir + "/" + job.slug + "_fit.txt", sb.ToString());
        }
    }
}
