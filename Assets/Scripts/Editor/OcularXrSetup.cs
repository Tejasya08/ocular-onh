// -----------------------------------------------------------------------------
// Ocular -- enabling the WebXR loader for WebGL builds.
//
// WebXR Export registers WebXRLoader with XR Plug-in Management, but registering
// is not enabling. Until the loader is ASSIGNED to the WebGL build target,
// XRGeneralSettings.Instance.Manager.activeLoader is null at runtime, the
// subsystem never starts, and WebXRManager.isSupportedVR is false forever. The
// page loads, the eye renders, the Enter VR button is there and greyed out, and
// nothing anywhere says why -- which is exactly the kind of silent dead end that
// makes people conclude "WebXR doesn't work in Unity".
//
// Doing it in code rather than through the XR Plug-in Management window means the
// setting travels with the repo instead of living in one machine's memory.
// -----------------------------------------------------------------------------

using UnityEditor;
using UnityEngine;
using Ocular.Core.Diagnostics;

#if OCULAR_XRMGMT
using UnityEngine.XR.Management;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
#endif

namespace Ocular.Editor
{
    public static class OcularXrSetup
    {
        private const string WebXrLoaderType = "WebXR.WebXRLoader";
        private const string SettingsDir = "Assets/XR";
        private const string SettingsPath = SettingsDir + "/XRGeneralSettingsPerBuildTarget.asset";

        /// <summary>The WebGL template that carries the WebXR JavaScript glue.</summary>
        public const string WebXrTemplate = "PROJECT:WebXRFullView2020";

        [MenuItem("Ocular/Configure WebXR", priority = 30)]
        public static void ConfigureWebXr()
        {
            ApplyWebGlTemplate();
            EnableWebXrLoader();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        /// <summary>
        /// Selects the WebXR WebGL template.
        ///
        /// <para>The default Unity template has no WebXR JavaScript in it at all, so
        /// a build made against it cannot enter an immersive session no matter how
        /// the loader is configured. The template files are copied out of the
        /// package's Hidden~ folder into Assets/WebGLTemplates.</para>
        /// </summary>
        public static void ApplyWebGlTemplate()
        {
            const string templateFolder = "Assets/WebGLTemplates/WebXRFullView2020";

            if (!AssetDatabase.IsValidFolder(templateFolder))
            {
                OLog.Error(LogCat.Build,
                    "WebXR template missing at " + templateFolder
                    + ". Run Window > WebXR > Copy WebGLTemplates, then re-run this.");
                return;
            }

            PlayerSettings.WebGL.template = WebXrTemplate;
            OLog.Info(LogCat.Build, "WebGL template set to " + WebXrTemplate);
        }

#if OCULAR_XRMGMT
        /// <summary>
        /// Creates the XR settings asset if absent and assigns WebXRLoader to the
        /// WebGL target.
        /// </summary>
        public static void EnableWebXrLoader()
        {
            XRGeneralSettingsPerBuildTarget perTarget;
            EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out perTarget);

            if (perTarget == null)
            {
                if (!AssetDatabase.IsValidFolder(SettingsDir))
                {
                    AssetDatabase.CreateFolder("Assets", "XR");
                }

                perTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(perTarget, SettingsPath);

                // Registering it as a CONFIG OBJECT is what makes the runtime find
                // it. An XR settings asset that merely exists in the project is
                // never loaded, and the failure looks identical to having no
                // settings at all.
                EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, perTarget, true);

                OLog.Info(LogCat.Build, "Created " + SettingsPath);
            }

            // Two separate creates: the settings object, and the manager inside it.
            // Creating only the manager leaves SettingsForBuildTarget returning null.
            if (!perTarget.HasSettingsForBuildTarget(BuildTargetGroup.WebGL))
            {
                perTarget.CreateDefaultSettingsForBuildTarget(BuildTargetGroup.WebGL);
            }

            if (!perTarget.HasManagerSettingsForBuildTarget(BuildTargetGroup.WebGL))
            {
                perTarget.CreateDefaultManagerSettingsForBuildTarget(BuildTargetGroup.WebGL);
            }

            XRGeneralSettings settings = perTarget.SettingsForBuildTarget(BuildTargetGroup.WebGL);

            if (settings == null || settings.Manager == null)
            {
                OLog.Error(LogCat.Build, "Could not create XR settings for WebGL.");
                return;
            }

            if (XRPackageMetadataStore.IsLoaderAssigned(WebXrLoaderType, BuildTargetGroup.WebGL))
            {
                OLog.Info(LogCat.Build, "WebXR loader already assigned to WebGL.");
            }
            else if (XRPackageMetadataStore.AssignLoader(settings.Manager, WebXrLoaderType, BuildTargetGroup.WebGL))
            {
                OLog.Info(LogCat.Build, "WebXR loader assigned to WebGL.");
            }
            else
            {
                OLog.Error(LogCat.Build, "Failed to assign " + WebXrLoaderType + " to WebGL.");
            }

            // Initialising on startup means the page can offer Enter VR immediately
            // rather than only after some other code happens to boot the subsystem.
            settings.InitManagerOnStart = true;

            EditorUtility.SetDirty(settings);
            EditorUtility.SetDirty(perTarget);
        }
#else
        public static void EnableWebXrLoader()
        {
            OLog.Warn(LogCat.Build,
                "com.unity.xr.management is not installed; WebXR loader cannot be enabled.");
        }
#endif
    }
}
