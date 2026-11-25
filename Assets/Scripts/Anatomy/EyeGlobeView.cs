// -----------------------------------------------------------------------------
// Ocular -- the eye around the disc. Drop a model in, it does the rest.
//
// WORKFLOW: put any .fbx / .obj / .blend into Assets/Resources/EyeGlobe/ and run.
// Nothing to wire, no scene to regenerate, no scale to work out. The component
// finds the model, measures it, sizes it to a real eye and parks it around the
// nerve head.
//
// It loads from Resources deliberately. The alternative -- a serialized
// reference filled in by the scene generator -- means dropping a new model does
// nothing until someone remembers to regenerate the scene, and "it did nothing
// and said nothing" is the worst failure mode a drop-in folder can have. One
// eye model is a few megabytes, which is a fair price for the folder just
// working. It also means the model resolves the same way in a player build.
//
// CONTEXT ONLY. This mesh is scenery. Not one number on screen comes from it --
// the metrics are measured off the parametric height field, which is the only
// surface in this project that is allowed to mean anything. An imported globe
// can be anatomically loose, low-poly or stylised without making a single
// measurement wrong, and the optic nerve head itself must NEVER be replaced this
// way for exactly the same reason.
//
// SCALE. The component is a child of the nerve head transform, so it inherits
// the magnification and works in the same millimetre-space the mesh builder
// uses: 1 mm = OnhMeshBuilder.MmToUnits. Sizing is derived from the model's own
// bounds rather than trusted from its import settings, because a model exported
// from Blender in metres and one exported from ZBrush in arbitrary units differ
// by three orders of magnitude and neither one tells you which it is.
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEngine.Rendering;
using Ocular.Core.Diagnostics;

namespace Ocular.Anatomy
{
    /// <summary>How much of the globe to draw. Cycled with G.</summary>
    public enum EyeGlobeMode
    {
        /// <summary>Translucent shell. The disc stays readable through it.</summary>
        Ghost = 0,

        /// <summary>Front faces culled, so the view falls through into the interior.</summary>
        Sectioned = 1,

        /// <summary>Ordinary opaque model. Hides the disc from most angles.</summary>
        Solid = 2,

        /// <summary>Not drawn at all.</summary>
        Hidden = 3
    }

    [DisallowMultipleComponent]
    public sealed class EyeGlobeView : MonoBehaviour
    {
        /// <summary>Drop models here. Any GameObject asset in this folder is a candidate.</summary>
        public const string ResourceFolder = "EyeGlobe";

        /// <summary>Axial length of an average adult eye, mm. What the model is scaled to.</summary>
        public const float DefaultEyeDiameterMm = 24.0f;

        [Header("Model")]
        [Tooltip("Leave empty to auto-load the first model found in Assets/Resources/EyeGlobe/.")]
        [SerializeField] private GameObject _explicitModel;

        [Tooltip("Longest dimension the model is scaled to, in millimetres.")]
        [SerializeField] private float _eyeDiameterMm = DefaultEyeDiameterMm;

        [Header("Placement")]
        [Tooltip("Extra rotation, degrees, if the model's optic axis is not +Y in its own space.")]
        [SerializeField] private Vector3 _modelRotationEuler = Vector3.zero;

        [Tooltip("Nudge along the optic axis in mm. Positive lifts the globe off the disc.")]
        [SerializeField] private float _axialOffsetMm = 0f;

        [Header("Display")]
        [SerializeField] private EyeGlobeMode _mode = EyeGlobeMode.Ghost;

        [Range(0.05f, 1f), Tooltip("Shell opacity in Ghost mode.")]
        [SerializeField] private float _ghostAlpha = 0.22f;

        [SerializeField] private KeyCode _cycleKey = KeyCode.G;

        private Transform _pivot;
        private GameObject _instance;
        private Renderer[] _renderers;
        private Material _material;

        public bool HasModel { get { return _instance != null; } }

        public EyeGlobeMode Mode
        {
            get { return _mode; }
            set { _mode = value; ApplyMode(); }
        }

        private void Start()
        {
            GameObject source = _explicitModel != null ? _explicitModel : FindDroppedModel();

            if (source == null)
            {
                // Not an error. No model is the normal state of a fresh checkout,
                // and the simulator is fully usable without one.
                OLog.Info(LogCat.Anatomy,
                    "No eye model found. Drop an .fbx or .obj into Assets/Resources/"
                    + ResourceFolder + "/ and it will appear around the disc.");
                return;
            }

            Build(source);
            ApplyMode();
        }

        private void Update()
        {
            if (_instance != null && Input.GetKeyDown(_cycleKey)) Cycle();
        }

        /// <summary>Steps to the next display mode, wrapping.</summary>
        public void Cycle()
        {
            _mode = (EyeGlobeMode)(((int)_mode + 1) % 4);
            ApplyMode();

            OLog.Info(LogCat.Anatomy, "Eye globe: " + _mode);
        }

        /// <summary>
        /// First model asset in the drop folder. Alphabetical, so two models give a
        /// stable answer rather than whichever one the importer happened to hash
        /// first -- a globe that changes between runs would be baffling.
        /// </summary>
        private static GameObject FindDroppedModel()
        {
            GameObject[] found = Resources.LoadAll<GameObject>(ResourceFolder);

            if (found == null || found.Length == 0) return null;

            GameObject best = found[0];

            for (int i = 1; i < found.Length; i++)
                if (string.CompareOrdinal(found[i].name, best.name) < 0) best = found[i];

            if (found.Length > 1)
            {
                OLog.Warn(LogCat.Anatomy, found.Length + " models in Resources/" + ResourceFolder
                    + "; using '" + best.name + "'. Keep one, or set the model explicitly.");
            }

            return best;
        }

        /// <summary>
        /// Takes an ALREADY-INSTANTIATED model and fits it like a dropped one.
        ///
        /// <para>This is the runtime path: a glTF downloaded from Tripo while the
        /// simulator is open arrives as a live GameObject, never as a Resources
        /// asset, so it cannot go through Build. Everything after instantiation --
        /// measuring, scaling, centring, re-materialling, stripping colliders --
        /// is identical, which is why that work lives in Configure and not in
        /// Build.</para>
        /// </summary>
        public void AdoptModel(GameObject instance)
        {
            if (instance == null) return;

            // Replace rather than accumulate: importing a second eye would
            // otherwise leave the first one inside the new one.
            if (_instance != null) Destroy(_instance);
            if (_pivot == null)
            {
                _pivot = new GameObject("EyeGlobe").transform;
                _pivot.SetParent(transform, false);
            }

            _instance = instance;
            _instance.transform.SetParent(_pivot, false);

            Configure(instance.name);

            // Solid, not the usual Ghost.
            //
            // Ghost is right for the model this component was built for -- a
            // decorative eyeball wrapped around the disc, which has to be
            // see-through or it hides the thing being examined. A model adopted
            // at run time is the opposite case: it was generated FROM the
            // patient's own photograph and showing it IS the point, so
            // presenting it at 22% opacity would hide the deliverable.
            _mode = EyeGlobeMode.Solid;
            ApplyMode();
        }

        private void Build(GameObject source)
        {
            _pivot = new GameObject("EyeGlobe").transform;
            _pivot.SetParent(transform, false);

            _instance = Instantiate(source, _pivot);
            _instance.name = source.name;

            Configure(source.name);
        }

        /// <summary>Measures, scales, places and re-materials whatever is in _instance.</summary>
        private void Configure(string sourceName)
        {
            Transform t = _instance.transform;
            t.localPosition = Vector3.zero;
            t.localRotation = Quaternion.Euler(_modelRotationEuler);
            t.localScale = Vector3.one;

            Bounds bounds;
            if (!TryMeasure(out bounds))
            {
                OLog.Warn(LogCat.Anatomy,
                    "Eye model '" + sourceName + "' has no renderable mesh; nothing to show.");
                return;
            }

            // Largest axis, not the mean. An eye is very nearly a sphere, so if a
            // model measures much longer on one axis it has something attached --
            // an optic nerve stump, most often -- and fitting to the mean would
            // blow the globe itself up past the real size.
            float largest = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));

            if (largest < 1e-6f)
            {
                OLog.Warn(LogCat.Anatomy, "Eye model '" + sourceName + "' has zero size.");
                return;
            }

            float targetUnits = _eyeDiameterMm * OnhMeshBuilder.MmToUnits;
            float k = targetUnits / largest;

            t.localScale = Vector3.one * k;

            // Centre the model's own bounds on the pivot, so an asset authored
            // off-origin -- which most are -- still ends up concentric.
            t.localPosition = -(t.localRotation * bounds.center) * k;

            // The retina is at y = 0 and +y points into the vitreous (the mesh
            // builder writes height along +y, with the cup going negative). So the
            // globe's centre sits one radius ABOVE the disc, putting the disc at
            // the posterior pole where it belongs, and the camera -- which looks
            // down the optic axis from the vitreous side -- ends up inside the eye
            // looking back at the nerve head, which is the view a clinician has.
            float radiusUnits = targetUnits * 0.5f;
            _pivot.localPosition = new Vector3(0f, radiusUnits + _axialOffsetMm * OnhMeshBuilder.MmToUnits, 0f);

            _renderers = _instance.GetComponentsInChildren<Renderer>(true);

            StripColliders();
            ApplyMaterial();

            OLog.Info(LogCat.Anatomy, string.Format(
                "Eye globe '{0}': model measured {1:0.###} units, scaled x{2:0.####} to {3:0.0} mm.",
                sourceName, largest, k, _eyeDiameterMm));
        }

        /// <summary>Combined mesh bounds in the instance's own local space.</summary>
        private bool TryMeasure(out Bounds bounds)
        {
            bounds = new Bounds();

            MeshFilter[] filters = _instance.GetComponentsInChildren<MeshFilter>(true);
            SkinnedMeshRenderer[] skinned = _instance.GetComponentsInChildren<SkinnedMeshRenderer>(true);

            Matrix4x4 toLocal = _instance.transform.worldToLocalMatrix;
            bool any = false;

            for (int i = 0; i < filters.Length; i++)
            {
                Mesh mesh = filters[i].sharedMesh;
                if (mesh == null) continue;

                Accumulate(ref bounds, ref any, mesh.bounds, toLocal * filters[i].transform.localToWorldMatrix);
            }

            for (int i = 0; i < skinned.Length; i++)
            {
                Mesh mesh = skinned[i].sharedMesh;
                if (mesh == null) continue;

                Accumulate(ref bounds, ref any, mesh.bounds, toLocal * skinned[i].transform.localToWorldMatrix);
            }

            return any;
        }

        /// <summary>
        /// Expands by all eight transformed corners rather than by the transformed
        /// centre and extents. A rotated child's axis-aligned box is not the
        /// rotation of its box, and using the latter under-measures a tilted model
        /// -- which would then be scaled too large and clip through the disc.
        /// </summary>
        private static void Accumulate(ref Bounds bounds, ref bool any, Bounds local, Matrix4x4 m)
        {
            Vector3 c = local.center;
            Vector3 e = local.extents;

            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = new Vector3(
                    c.x + ((i & 1) == 0 ? -e.x : e.x),
                    c.y + ((i & 2) == 0 ? -e.y : e.y),
                    c.z + ((i & 4) == 0 ? -e.z : e.z));

                Vector3 p = m.MultiplyPoint3x4(corner);

                if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; }
                else bounds.Encapsulate(p);
            }
        }

        /// <summary>
        /// Imported models often arrive with mesh colliders enabled. The orbit
        /// controller and the planimetry tool both raycast at the disc, and a
        /// collider wrapped around the entire eye swallows every one of those rays
        /// -- the tool silently stops working and the globe looks innocent.
        /// </summary>
        private void StripColliders()
        {
            Collider[] colliders = _instance.GetComponentsInChildren<Collider>(true);

            for (int i = 0; i < colliders.Length; i++) Destroy(colliders[i]);
        }

        /// <summary>
        /// One shared URP material for the whole globe, replacing whatever the
        /// model shipped with.
        ///
        /// <para>A model authored against the built-in pipeline renders MAGENTA in
        /// URP, and that is the single most common "my import is broken" outcome.
        /// Overriding rather than converting also means the globe's look is the
        /// project's decision, so a downloaded asset with baked-in eyelashes and a
        /// photographic iris does not drag its art direction into a clinical
        /// tool.</para>
        /// </summary>
        private void ApplyMaterial()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");

            if (shader == null)
            {
                OLog.Warn(LogCat.Anatomy, "No URP shader available; the globe keeps its own materials.");
                return;
            }

            _material = new Material(shader);
            _material.name = "EyeGlobe (instance)";

            // Sclera: not white. A pure white shell next to a light background
            // disappears into it, and next to the disc it reads brighter than the
            // tissue, which is the one thing the background colour was chosen to
            // avoid.
            SetColour(new Color(0.86f, 0.84f, 0.80f, 1f));

            if (_material.HasProperty("_Smoothness")) _material.SetFloat("_Smoothness", 0.28f);
            if (_material.HasProperty("_Metallic")) _material.SetFloat("_Metallic", 0f);

            for (int i = 0; i < _renderers.Length; i++)
            {
                Material[] slots = new Material[_renderers[i].sharedMaterials.Length];
                for (int s = 0; s < slots.Length; s++) slots[s] = _material;

                _renderers[i].sharedMaterials = slots;

                // The globe must never cast onto the disc. A shadow across the cup
                // wall is indistinguishable from cup depth, and depth read off
                // shading is the whole point of the view.
                _renderers[i].shadowCastingMode = ShadowCastingMode.Off;
                _renderers[i].receiveShadows = false;
            }
        }

        private void SetColour(Color c)
        {
            if (_material.HasProperty("_BaseColor")) _material.SetColor("_BaseColor", c);
            if (_material.HasProperty("_Color")) _material.SetColor("_Color", c);
        }

        private void ApplyMode()
        {
            if (_renderers == null || _material == null) return;

            bool visible = _mode != EyeGlobeMode.Hidden;

            for (int i = 0; i < _renderers.Length; i++) _renderers[i].enabled = visible;

            if (!visible) return;

            switch (_mode)
            {
                case EyeGlobeMode.Ghost:
                    SetTransparent(true);
                    SetCull(CullMode.Back);
                    break;

                case EyeGlobeMode.Sectioned:
                    SetTransparent(false);
                    SetCull(CullMode.Front);   // drop the near wall, look inside
                    break;

                default:
                    SetTransparent(false);
                    SetCull(CullMode.Back);
                    break;
            }
        }

        private void SetCull(CullMode cull)
        {
            if (_material.HasProperty("_Cull")) _material.SetFloat("_Cull", (float)cull);
        }

        /// <summary>
        /// Flips URP/Lit between opaque and alpha-blended at runtime.
        ///
        /// <para>Setting _Surface alone does nothing: the surface type is a shader
        /// FEATURE, so the blend factors, ZWrite, the keyword and the render queue
        /// all have to be set together. Miss any one of them and the material
        /// stays stubbornly opaque with no warning -- which is why this is spelled
        /// out rather than done through a material variant.</para>
        /// </summary>
        private void SetTransparent(bool transparent)
        {
            Color c = _material.HasProperty("_BaseColor")
                ? _material.GetColor("_BaseColor")
                : Color.white;

            c.a = transparent ? _ghostAlpha : 1f;

            if (transparent)
            {
                _material.SetFloat("_Surface", 1f);
                _material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                _material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                _material.SetFloat("_ZWrite", 0f);
                _material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                _material.renderQueue = (int)RenderQueue.Transparent;
            }
            else
            {
                _material.SetFloat("_Surface", 0f);
                _material.SetFloat("_SrcBlend", (float)BlendMode.One);
                _material.SetFloat("_DstBlend", (float)BlendMode.Zero);
                _material.SetFloat("_ZWrite", 1f);
                _material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
                _material.renderQueue = (int)RenderQueue.Geometry;
            }

            SetColour(c);
        }

        private void OnDestroy()
        {
            if (_material != null) Destroy(_material);
        }
    }
}
