// -----------------------------------------------------------------------------
// Ocular -- project settings configuration.
//
// The one that actually matters is ConfigureRenderPipeline. Without a URP asset
// assigned, Shader.Find("Universal Render Pipeline/Lit") still RESOLVES in the
// editor -- the shader is loaded, so the material looks fine in the scene view --
// but the player build runs the built-in renderer, strips every URP variant, and
// the whole eye ships magenta. It is a failure that only appears in the build,
// which is the worst place to find it.
// -----------------------------------------------------------------------------

using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Ocular.Core.Diagnostics;

namespace Ocular.Editor
{
    public static class OcularProjectSettings
    {
        private const string SettingsDir = "Assets/Settings";
        private const string PipelinePath = SettingsDir + "/OcularURP.asset";
        private const string RendererPath = SettingsDir + "/OcularURP_Renderer.asset";

        [MenuItem("Ocular/Configure Project Settings", priority = 10)]
        public static void ConfigureAll()
        {
            ConfigureSerialization();
            ConfigureRenderPipeline();
            ConfigureAlwaysIncludedShaders();
            ConfigurePlayer();
            ConfigureInputHandler();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            OLog.Info(LogCat.Build, "Ocular project settings configured.");
        }

        /// <summary>
        /// Creates and assigns the URP pipeline asset to BOTH GraphicsSettings and
        /// QualitySettings. Setting only one leaves some quality tiers on the
        /// built-in renderer, which produces a partial magenta that looks like a
        /// material bug rather than a pipeline one.
        /// </summary>
        private static void ConfigureRenderPipeline()
        {
            if (!AssetDatabase.IsValidFolder(SettingsDir))
            {
                AssetDatabase.CreateFolder("Assets", "Settings");
            }

            UniversalRenderPipelineAsset pipeline =
                AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);

            if (pipeline == null)
            {
                UniversalRendererData rendererData =
                    AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);

                if (rendererData == null)
                {
                    rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
                    AssetDatabase.CreateAsset(rendererData, RendererPath);
                }

                pipeline = UniversalRenderPipelineAsset.Create(rendererData);
                AssetDatabase.CreateAsset(pipeline, PipelinePath);

                OLog.Info(LogCat.Build, "Created URP pipeline asset.");
            }

            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;

            // The subject is a single small object under controlled light. Long
            // shadow distances and cascades buy nothing here and cost real frame
            // time on an integrated GPU and in WebGL.
            pipeline.shadowDistance = 8f;
            pipeline.supportsHDR = false;
            pipeline.msaaSampleCount = 4;

            EditorUtility.SetDirty(pipeline);
            OLog.Info(LogCat.Build, "URP assigned to GraphicsSettings and QualitySettings.");
        }

        /// <summary>
        /// Keeps the big URP shaders OUT of Always Included Shaders.
        ///
        /// <para>The player needs URP/Lit to exist, and the honest way to guarantee
        /// that is for the SCENE to reference a material asset that uses it --
        /// which OcularSceneGenerator now creates. Always Included Shaders looks
        /// like an easier fix and is a trap: it forces Unity to compile EVERY
        /// variant of the shader rather than the handful the project uses. Measured
        /// here, that was 294,912 variants after stripping and a build that had not
        /// finished compiling them after nine minutes on this machine.</para>
        ///
        /// <para>This prunes rather than merely declining to add, because an earlier
        /// build already wrote those entries into GraphicsSettings.asset and they
        /// would otherwise persist forever.</para>
        /// </summary>
        private static void ConfigureAlwaysIncludedShaders()
        {
            string[] unwanted =
            {
                "Universal Render Pipeline/Lit",
                "Universal Render Pipeline/Unlit"
            };

            Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
            if (assets == null || assets.Length == 0) return;

            SerializedObject so = new SerializedObject(assets[0]);
            SerializedProperty list = so.FindProperty("m_AlwaysIncludedShaders");
            if (list == null) return;

            bool changed = false;

            for (int u = 0; u < unwanted.Length; u++)
            {
                Shader shader = Shader.Find(unwanted[u]);
                if (shader == null) continue;

                // Backwards: removing shifts everything after the removed index.
                for (int i = list.arraySize - 1; i >= 0; i--)
                {
                    if (list.GetArrayElementAtIndex(i).objectReferenceValue != shader) continue;

                    // An object-reference element must be nulled before deletion or
                    // DeleteArrayElementAtIndex only clears the reference and leaves
                    // an empty slot behind.
                    list.GetArrayElementAtIndex(i).objectReferenceValue = null;
                    list.DeleteArrayElementAtIndex(i);

                    changed = true;
                    OLog.Info(LogCat.Build, "Removed always-included shader: " + unwanted[u]);
                }
            }

            if (changed) so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigurePlayer()
        {
            PlayerSettings.companyName = "Ocular";
            PlayerSettings.productName = "Ocular - Glaucoma ONH Simulator";

            // Linear is not cosmetic here: the cup wall is read by its shading
            // gradient, and gamma space crushes exactly the mid-tones that carry it.
            PlayerSettings.colorSpace = ColorSpace.Linear;

            PlayerSettings.runInBackground = true;
            PlayerSettings.resizableWindow = true;

            OLog.Info(LogCat.Build, "Player settings configured (linear colour space).");
        }

        /// <summary>
        /// Allows BOTH input backends.
        ///
        /// <para>The manifest pulls in the Input System package, and if the project
        /// ever flips to new-input-only then every legacy UnityEngine.Input call --
        /// which is what OrbitInspector uses -- throws InvalidOperationException at
        /// runtime and the camera dies. There is no PlayerSettings API for this, so
        /// it goes through the serialised project settings object.</para>
        /// </summary>
        private static void ConfigureInputHandler()
        {
            Object[] settings = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
            if (settings == null || settings.Length == 0) return;

            SerializedObject so = new SerializedObject(settings[0]);
            SerializedProperty handler = so.FindProperty("activeInputHandler");

            if (handler == null) return;

            // 0 = old, 1 = new, 2 = both.
            if (handler.intValue != 2)
            {
                handler.intValue = 2;
                so.ApplyModifiedPropertiesWithoutUndo();
                OLog.Info(LogCat.Build, "Input handling set to Both (legacy + Input System).");
            }
        }

        /// <summary>
        /// Forces text serialization so scenes and assets are diffable and
        /// reviewable -- which is the entire point of generating them from code.
        /// </summary>
        private static void ConfigureSerialization()
        {
            if (EditorSettings.serializationMode != SerializationMode.ForceText)
            {
                EditorSettings.serializationMode = SerializationMode.ForceText;
                OLog.Info(LogCat.Build, "Asset serialization set to ForceText.");
            }
        }
    }
}
