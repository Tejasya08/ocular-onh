// -----------------------------------------------------------------------------
// Ocular -- builds the playable scene from code.
//
// THIS IS THE REGENERATION GATE. The scene is not hand-authored, so after
// changing a case preset or the scene layout you must re-run this or you are
// testing the previous build. A clean compile does NOT mean the scene changed.
//
// Everything is created programmatically rather than shipped as a .unity file
// full of GUIDs, which means the scene is reviewable as the code that produced
// it and cannot drift from the presets it was generated from.
// -----------------------------------------------------------------------------

using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Ocular.Anatomy;
using Ocular.Core.Diagnostics;
using Ocular.Data;
using Ocular.Interaction;
using Ocular.Pipeline;

namespace Ocular.Editor
{
    public static class OcularSceneGenerator
    {
        private const string ScenesDir = "Assets/Scenes";
        private const string CasesDir = "Assets/Cases";
        private const string ScenePath = ScenesDir + "/OcularSim.unity";
        private const string LibraryPath = CasesDir + "/CaseLibrary.asset";

        /// <summary>
        /// The field the specimen sits on: a bright, faintly cool neutral, the way
        /// a slit-lamp photograph or a viewing box reads rather than the dark
        /// void a 3D viewer defaults to.
        ///
        /// <para>Deliberately NOT pure white. At 1.0 the background is brighter
        /// than any lit part of the disc, so the eye adapts to the surround and the
        /// tissue reads as a dark silhouette -- and the cup wall's shading gradient,
        /// which is the entire depth cue here, is the first thing to go. Sitting a
        /// few percent under white keeps the specimen the brightest thing in
        /// frame.</para>
        ///
        /// <para>Defined once because two cameras use it -- the desktop inspector
        /// and the XR rig -- and a background that matches in the editor but not in
        /// a headset is a difference nobody notices until they are wearing one.</para>
        /// </summary>
        private static readonly Color ClinicalBackground = new Color(0.90f, 0.91f, 0.93f);

        [MenuItem("Ocular/Generate Simulation Scene", priority = 20)]
        public static void GenerateMenu()
        {
            Generate();
            EditorUtility.DisplayDialog("Ocular",
                "Simulation scene generated at\n" + ScenePath, "OK");
        }

        /// <summary>CI entry point: -executeMethod Ocular.Editor.OcularSceneGenerator.GenerateBatch</summary>
        public static void GenerateBatch()
        {
            try
            {
                Generate();
            }
            catch (System.Exception ex)
            {
                OLog.Error(LogCat.Build, "Scene generation threw: " + ex);
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }

            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        public static void Generate()
        {
            // Settings first. The materials created during scene build bind to
            // whichever pipeline is active at that moment.
            OcularProjectSettings.ConfigureAll();

            EnsureFolder(ScenesDir);
            EnsureFolder(CasesDir);

            CaseLibrary library = BuildCaseLibrary();

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            BuildLighting();
            OnhModelBinding onh = BuildOpticNerveHead(library);

            // Added unconditionally, with no model reference. The component looks in
            // Assets/Resources/EyeGlobe/ at run time and does nothing when it is
            // empty -- so dropping a model in later needs no regeneration, and a
            // checkout with no model is not a broken scene.
            onh.gameObject.AddComponent<EyeGlobeView>();

            // Same arrangement, same reason: it finds an imported patient in
            // Resources at startup and does nothing when there is none, so the
            // import pipeline never has to regenerate or rebuild the scene.
            onh.gameObject.AddComponent<StudyAutoLoader>();

            // The in-app import path: file picker, Tripo call, runtime glTF load.
            // Added before the HUD is built so BuildHud can find and bind it.
            onh.gameObject.AddComponent<EyeImportController>();

            OrbitInspector camera = BuildCamera(onh.transform);
            DesktopPointer pointer = camera.GetComponent<DesktopPointer>();
            PlanimetryTool planimetry = BuildPlanimetry(onh, pointer);
            XrSessionController xr = BuildXrRig(onh, camera, pointer);
            BuildHud(onh, library, camera, planimetry, xr);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(ScenePath)));
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);

            RegisterInBuildSettings();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            OLog.Info(LogCat.Build, "Generated " + ScenePath + " with " + library.Count + " cases.");
        }

        // =====================================================================

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;

            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            string leaf = Path.GetFileName(path);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        /// <summary>
        /// Writes every preset out as its own asset and collects them into a
        /// library. Regenerating overwrites in place so that a case already
        /// referenced by a scene keeps its GUID and the reference survives.
        /// </summary>
        private static CaseLibrary BuildCaseLibrary()
        {
            GlaucomaCase[] presets = CasePresets.BuildAll();

            CaseLibrary library = AssetDatabase.LoadAssetAtPath<CaseLibrary>(LibraryPath);
            if (library == null)
            {
                library = ScriptableObject.CreateInstance<CaseLibrary>();
                AssetDatabase.CreateAsset(library, LibraryPath);
            }

            library.Cases.Clear();

            for (int i = 0; i < presets.Length; i++)
            {
                GlaucomaCase preset = presets[i];
                string path = CasesDir + "/" + preset.name + ".asset";

                GlaucomaCase existing = AssetDatabase.LoadAssetAtPath<GlaucomaCase>(path);

                if (existing == null)
                {
                    AssetDatabase.CreateAsset(preset, path);
                    existing = preset;
                }
                else
                {
                    // Copy the freshly built values onto the existing asset rather
                    // than replacing it, so the GUID -- and every reference to it --
                    // survives regeneration.
                    EditorUtility.CopySerialized(preset, existing);
                    Object.DestroyImmediate(preset);
                    EditorUtility.SetDirty(existing);
                }

                library.Cases.Add(existing);
            }

            EditorUtility.SetDirty(library);
            AssetDatabase.SaveAssets();

            return library;
        }

        /// <summary>
        /// Creates (or reuses) the fundus material as a real asset. OnhModelBinding
        /// clones this at runtime and swaps the generated texture onto the clone.
        /// </summary>
        private static Material EnsureFundusMaterial()
        {
            const string path = "Assets/Settings/FundusMaterial.mat";

            EnsureFolder("Assets/Settings");

            Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat != null) return mat;

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                OLog.Error(LogCat.Build,
                    "URP/Lit not resolvable at generate time. Run Ocular > Configure Project Settings.");
                return null;
            }

            mat = new Material(shader);
            mat.name = "FundusMaterial";
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.35f);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);

            AssetDatabase.CreateAsset(mat, path);
            OLog.Info(LogCat.Build, "Created " + path);

            return mat;
        }

        private static void BuildLighting()
        {
            GameObject go = new GameObject("Key Light");
            Light light = go.AddComponent<Light>();

            light.type = LightType.Directional;
            light.intensity = 1.15f;
            light.color = new Color(1f, 0.97f, 0.93f);
            light.shadows = LightShadows.Soft;

            // Raking rather than head-on. A light down the view axis flattens the
            // cup completely -- the depth cue IS the shading gradient on the wall,
            // and that is the one thing a fundus photograph cannot give you.
            go.transform.rotation = Quaternion.Euler(52f, 145f, 0f);

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;

            // Lifted to sit on the bright clinical background rather than the dark
            // void this started as: against a light surround the eye adapts upward,
            // and tissue lit for a dark room reads as a muddy silhouette.
            //
            // Lifted only PART of the way, though, and this is the trade-off worth
            // knowing about. Ambient is omnidirectional, so every increment fills
            // the cup's shaded wall and flattens the gradient across it -- and that
            // gradient is the depth cue, the one thing this tool has that a fundus
            // photograph does not. Take ambient much past here and the cup stops
            // reading as a cup. Brightness is worth having; it is not worth the cup.
            RenderSettings.ambientLight = new Color(0.42f, 0.40f, 0.40f);
        }

        private static OnhModelBinding BuildOpticNerveHead(CaseLibrary library)
        {
            GameObject go = new GameObject("OpticNerveHead");
            go.transform.position = Vector3.zero;

            go.AddComponent<MeshFilter>();
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();

            // The scene must REFERENCE the material as an asset. A shader that only
            // ever appears inside a Shader.Find string is invisible to the build
            // system and gets stripped from the player, which is what made the first
            // Windows build ship an invisible nerve head.
            renderer.sharedMaterial = EnsureFundusMaterial();

            OnhModelBinding binding = go.AddComponent<OnhModelBinding>();

            // Private [SerializeField] fields cannot be set from outside the class,
            // and widening them to public just for the generator would put a mutable
            // surface on the runtime API for the sake of an editor script.
            SerializedObject so = new SerializedObject(binding);
            SetObject(so, "_case", library.Count > 0 ? library.At(0) : null);
            SetFloat(so, "_magnification", 120f);
            SetInt(so, "_sectors", OnhMeshBuilder.DefaultSectors);
            SetInt(so, "_rings", OnhMeshBuilder.DefaultRings);
            SetInt(so, "_textureResolution", FundusTextureBuilder.DefaultResolution);
            so.ApplyModifiedPropertiesWithoutUndo();

            return binding;
        }

        /// <summary>
        /// The HUD builds its own canvas and labels at runtime, so this only has to
        /// hand it the three things it cannot find for itself.
        /// </summary>
        private static void BuildHud(OnhModelBinding onh, CaseLibrary library,
                                     OrbitInspector camera, PlanimetryTool planimetry,
                                     XrSessionController xr)
        {
            GameObject go = new GameObject("HUD");
            Ocular.UI.OcularHud hud = go.AddComponent<Ocular.UI.OcularHud>();

            SerializedObject so = new SerializedObject(hud);
            SetObject(so, "_model", onh);
            SetObject(so, "_library", library);
            SetObject(so, "_camera", camera);
            SetObject(so, "_planimetry", planimetry);
            SetObject(so, "_xr", xr);
            SetObject(so, "_import", onh.GetComponent<EyeImportController>());
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>
        /// Builds the WebXR camera rig and the session controller.
        ///
        /// <para>WebXRCamera needs FIVE cameras: the normal one, a stereo pair for
        /// VR, and a second stereo pair for AR. It enables whichever set the current
        /// session needs and leaves the rest off, so they are all created disabled
        /// except the main one.</para>
        ///
        /// <para>The rig transform is the TRACKING ORIGIN -- floor level in a
        /// room-scale session -- which is why XrSessionController parks the model at
        /// roughly chest height rather than at the world origin when a session
        /// starts. A model left at y=0 in VR sits on the floor at your feet.</para>
        /// </summary>
        private static XrSessionController BuildXrRig(OnhModelBinding onh, OrbitInspector orbit,
                                                      DesktopPointer pointer)
        {
#if OCULAR_WEBXR
            // MUST exist before WebXRCamera.OnEnable runs: that method dereferences
            // WebXRManager.Instance unconditionally, so a scene without a manager
            // throws NullReferenceException on the first frame and the whole rig
            // dies silently. WebXRManager carries [DefaultExecutionOrder(-2020)],
            // so simply having it in the scene is enough to order it correctly.
            GameObject managerGo = new GameObject("WebXR Manager");
            managerGo.AddComponent<WebXR.WebXRManager>();
#endif

            GameObject rig = new GameObject("XR Rig");
            Camera mainCamera = orbit.GetComponent<Camera>();

            // The desktop camera becomes the rig's main camera. Parenting it keeps
            // every camera in one tracking space.
            mainCamera.transform.SetParent(rig.transform, true);

            Camera vrLeft = MakeRigCamera(rig.transform, "CameraL");
            Camera vrRight = MakeRigCamera(rig.transform, "CameraR");
            Camera arLeft = MakeRigCamera(rig.transform, "CameraARL");
            Camera arRight = MakeRigCamera(rig.transform, "CameraARR");

#if OCULAR_WEBXR
            WebXR.WebXRCamera webXrCamera = rig.AddComponent<WebXR.WebXRCamera>();

            SerializedObject cso = new SerializedObject(webXrCamera);
            SetObject(cso, "cameraMain", mainCamera);
            SetObject(cso, "cameraL", vrLeft);
            SetObject(cso, "cameraR", vrRight);
            SetObject(cso, "cameraARL", arLeft);
            SetObject(cso, "cameraARR", arRight);

            // Both OFF, and this matters. Left on, WebXRCamera rewrites the main
            // camera's local pose and field of view every frame in NORMAL state,
            // which fights OrbitInspector for control and makes the desktop camera
            // jitter or lock up entirely.
            SetBool(cso, "m_updateNormalLocalPose", false);
            SetBool(cso, "m_updateNormalFieldOfView", false);

            cso.ApplyModifiedPropertiesWithoutUndo();

            BuildXrController(rig.transform, "Controller L", XrHand.Left, pointer);
            BuildXrController(rig.transform, "Controller R", XrHand.Right, pointer);
#endif

            GameObject sessionGo = new GameObject("XR Session");
            XrSessionController session = sessionGo.AddComponent<XrSessionController>();

            SerializedObject sso = new SerializedObject(session);
            SetObject(sso, "_model", onh);
            SetObject(sso, "_camera", mainCamera);
            SetObject(sso, "_orbit", orbit);
            sso.ApplyModifiedPropertiesWithoutUndo();

            return session;
        }

        private static Camera MakeRigCamera(Transform parent, string name)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);

            Camera cam = go.AddComponent<Camera>();
            cam.nearClipPlane = 0.02f;
            cam.farClipPlane = 50f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = ClinicalBackground;

            // The COMPONENT is disabled, not the GameObject -- and that distinction
            // is the whole bug. WebXRCamera.SwitchXRState only ever writes
            // camera.enabled; it never calls SetActive. A deactivated GameObject
            // would therefore stay dark forever no matter what the session did, and
            // the failure is invisible until someone actually puts a headset on.
            cam.enabled = false;

            return cam;
        }

#if OCULAR_WEBXR
        private static void BuildXrController(Transform parent, string name, XrHand hand,
                                              DesktopPointer fallback)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);

            WebXR.WebXRController controller = go.AddComponent<WebXR.WebXRController>();

            // Public field, so no SerializedObject dance needed. NONE is the default
            // and a controller left on NONE never reports a pose, which would look
            // exactly like a tracking failure.
            controller.hand = (hand == XrHand.Left)
                ? WebXR.WebXRControllerHand.LEFT
                : WebXR.WebXRControllerHand.RIGHT;

            XrPointer xrPointer = go.AddComponent<XrPointer>();

            SerializedObject pso = new SerializedObject(xrPointer);
            SetEnum(pso, "_hand", (int)hand);

            // The desktop pointer is the fallback so this component still drives the
            // same interaction model when no session is running.
            SetObject(pso, "_desktopFallback", fallback);
            pso.ApplyModifiedPropertiesWithoutUndo();
        }
#endif

        private static PlanimetryTool BuildPlanimetry(OnhModelBinding onh, DesktopPointer pointer)
        {
            GameObject go = new GameObject("PlanimetryTool");
            PlanimetryTool tool = go.AddComponent<PlanimetryTool>();

            SerializedObject so = new SerializedObject(tool);
            SetObject(so, "_model", onh);

            // Stored as a MonoBehaviour because Unity cannot serialise an interface
            // reference; PlanimetryTool casts it back to IPointerSource in Awake and
            // logs if the assigned component does not implement it.
            SetObject(so, "_pointerBehaviour", pointer);
            so.ApplyModifiedPropertiesWithoutUndo();

            return tool;
        }

        private static OrbitInspector BuildCamera(Transform target)
        {
            GameObject go = new GameObject("Inspector Camera");
            Camera cam = go.AddComponent<Camera>();

            cam.tag = "MainCamera";
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = ClinicalBackground;
            cam.fieldOfView = 45f;

            // The model is about 20cm across at the default magnification and the
            // user gets to within 12cm of it, so the default 0.3 near plane would
            // clip the whole disc away at close range.
            cam.nearClipPlane = 0.02f;
            cam.farClipPlane = 50f;

            OrbitInspector orbit = go.AddComponent<OrbitInspector>();

            SerializedObject so = new SerializedObject(orbit);
            SetObject(so, "_target", target);
            so.ApplyModifiedPropertiesWithoutUndo();

            // Lives on the camera because a mouse ray IS a camera ray. The XR
            // pointer will be a sibling on the controller instead, and nothing
            // downstream has to care which one is present.
            DesktopPointer pointer = go.AddComponent<DesktopPointer>();
            SerializedObject pso = new SerializedObject(pointer);
            SetObject(pso, "_camera", cam);
            pso.ApplyModifiedPropertiesWithoutUndo();

            orbit.Frame(FramingPreset.Oblique);
            return orbit;
        }

        private static void RegisterInBuildSettings()
        {
            EditorBuildSettingsScene[] existing = EditorBuildSettings.scenes;

            for (int i = 0; i < existing.Length; i++)
            {
                if (existing[i].path == ScenePath) return;
            }

            EditorBuildSettingsScene[] updated = new EditorBuildSettingsScene[existing.Length + 1];
            System.Array.Copy(existing, updated, existing.Length);
            updated[existing.Length] = new EditorBuildSettingsScene(ScenePath, true);

            EditorBuildSettings.scenes = updated;
            OLog.Info(LogCat.Build, "Registered " + ScenePath + " in Build Settings.");
        }

        // --- SerializedObject helpers that complain instead of failing quietly ---

        private static void SetObject(SerializedObject so, string field, Object value)
        {
            SerializedProperty p = so.FindProperty(field);
            if (p == null) { OLog.Error(LogCat.Build, "No serialized field '" + field + "'"); return; }
            p.objectReferenceValue = value;
        }

        private static void SetFloat(SerializedObject so, string field, float value)
        {
            SerializedProperty p = so.FindProperty(field);
            if (p == null) { OLog.Error(LogCat.Build, "No serialized field '" + field + "'"); return; }
            p.floatValue = value;
        }

        private static void SetInt(SerializedObject so, string field, int value)
        {
            SerializedProperty p = so.FindProperty(field);
            if (p == null) { OLog.Error(LogCat.Build, "No serialized field '" + field + "'"); return; }
            p.intValue = value;
        }

        private static void SetBool(SerializedObject so, string field, bool value)
        {
            SerializedProperty p = so.FindProperty(field);
            if (p == null) { OLog.Error(LogCat.Build, "No serialized field '" + field + "'"); return; }
            p.boolValue = value;
        }

        private static void SetEnum(SerializedObject so, string field, int index)
        {
            SerializedProperty p = so.FindProperty(field);
            if (p == null) { OLog.Error(LogCat.Build, "No serialized field '" + field + "'"); return; }
            p.enumValueIndex = index;
        }
    }
}
