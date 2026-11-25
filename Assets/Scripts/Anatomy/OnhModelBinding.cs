// -----------------------------------------------------------------------------
// Ocular -- the one object the rest of the app talks to the anatomy through.
//
// Owns the surface, the mesh, the material and the case. Everything else (HUD,
// overlays, planimetry, measurement) subscribes to GeometryRebuilt rather than
// polling or caching, because the whole point of the parameter panel is that the
// disc changes under them while they are on screen.
//
// The split between RebuildGeometry and RebuildAppearance is the important design
// decision here. Geometry is cheap enough to rebuild every frame during a slider
// drag; the fundus texture is a quarter of a million texels of managed loop and
// must not be. Anything that calls the wrong one turns a smooth drag into a
// slideshow, so they are named to make the mistake obvious.
// -----------------------------------------------------------------------------

using System;
using UnityEngine;
using Ocular.Core.Anatomy;
using Ocular.Core.Diagnostics;
using Ocular.Core.Imaging;
using Ocular.Core.Measurement;
using Ocular.Data;

namespace Ocular.Anatomy
{
    [RequireComponent(typeof(MeshFilter))]
    [RequireComponent(typeof(MeshRenderer))]
    public sealed class OnhModelBinding : MonoBehaviour
    {
        [Header("Case")]
        [SerializeField] private GlaucomaCase _case;

        [Header("Presentation")]
        [Tooltip("How much life-size anatomy is magnified for viewing. 120 makes a 1.8mm disc about 21cm across.")]
        [SerializeField] private float _magnification = 120f;

        [Header("Tessellation")]
        [SerializeField] private int _sectors = OnhMeshBuilder.DefaultSectors;
        [SerializeField] private int _rings = OnhMeshBuilder.DefaultRings;

        [Header("Appearance")]
        [SerializeField] private int _textureResolution = FundusTextureBuilder.DefaultResolution;

        [Tooltip("Optional imported eye. When set and readable, its photograph is projected onto "
               + "the surface instead of the procedural fundus. Appearance only -- the measured "
               + "numbers come from the height field either way.")]
        [SerializeField] private FundusStudy _study;

        private OnhMeshBuilder _builder;
        private Mesh _mesh;
        private Material _material;
        private Texture2D _fundus;

        private OpticNerveHeadSpec _spec = OpticNerveHeadSpec.Healthy();
        private Laterality _laterality = Laterality.OD;

        /// <summary>Raised after the mesh has been rebuilt. Redraw anything derived from it.</summary>
        public event Action<OnhModelBinding> GeometryRebuilt;

        public OnhSurface Surface { get; private set; }
        public GlaucomaCase Case { get { return _case; } }
        public Laterality Laterality { get { return _laterality; } }

        /// <summary>
        /// The authored spec. Assigning rebuilds the geometry but NOT the texture,
        /// so this is safe to drive from a slider every frame.
        /// </summary>
        public OpticNerveHeadSpec Spec
        {
            get { return _spec; }
            set
            {
                _spec = value;
                RebuildGeometry();
            }
        }

        public float Magnification
        {
            get { return _magnification; }
            set
            {
                _magnification = Mathf.Max(1f, value);
                ApplyMagnification();
            }
        }

        private void Awake()
        {
            _builder = new OnhMeshBuilder(_sectors, _rings);

            _mesh = new Mesh();
            _mesh.name = "OpticNerveHead";
            // 128 x 96 is over 12k vertices; the 16-bit index buffer tops out at
            // 65535 and a silent overflow shows up as scrambled triangles.
            _mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            GetComponent<MeshFilter>().sharedMesh = _mesh;

            EnsureMaterial();
            ApplyMagnification();
        }

        private void Start()
        {
            if (_case != null) LoadCase(_case);
            else RebuildAll();
        }

        private void OnDestroy()
        {
            // Procedurally created assets are not owned by the scene, so they leak
            // on every domain reload if they are not explicitly destroyed.
            if (_mesh != null) Destroy(_mesh);
            if (_material != null) Destroy(_material);
            if (_fundus != null) Destroy(_fundus);
        }

        /// <summary>
        /// Produces the instance material this disc renders with.
        ///
        /// <para>Clones the material the SCENE already references rather than
        /// conjuring one from Shader.Find. This is not a style preference: a shader
        /// named only by a runtime string is referenced by nothing the build system
        /// can see, so it gets stripped from the player. The first Windows build did
        /// exactly that -- Shader.Find returned null, the "Standard" fallback was
        /// null too (it belongs to the built-in pipeline, which a URP build does not
        /// ship), and `new Material(null)` threw ArgumentNullException in Awake,
        /// leaving an invisible nerve head and a working HUD. A material asset
        /// assigned in the scene keeps its shader alive through stripping.</para>
        /// </summary>
        private void EnsureMaterial()
        {
            if (_material != null) return;

            MeshRenderer renderer = GetComponent<MeshRenderer>();

            if (renderer != null && renderer.sharedMaterial != null)
            {
                _material = new Material(renderer.sharedMaterial);
            }
            else
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null) shader = Shader.Find("Sprites/Default");

                if (shader == null)
                {
                    // Returning is survivable - the disc renders untextured. Throwing
                    // here takes the whole Awake down and the scene never finishes.
                    OLog.Error(LogCat.Anatomy,
                        "No usable shader in this build; the nerve head cannot render. "
                        + "Run Ocular > Configure Project Settings and rebuild.");
                    return;
                }

                OLog.Warn(LogCat.Anatomy, "Scene had no material; created one from " + shader.name);
                _material = new Material(shader);
            }

            _material.name = "FundusMaterial (instance)";

            // Fundus tissue is wet but not mirror-like. High smoothness on a
            // magnified surface reads as plastic and destroys the depth cue that
            // the cup wall shading provides.
            if (_material.HasProperty("_Smoothness")) _material.SetFloat("_Smoothness", 0.35f);
            if (_material.HasProperty("_Metallic")) _material.SetFloat("_Metallic", 0f);

            GetComponent<MeshRenderer>().sharedMaterial = _material;
        }

        private void ApplyMagnification()
        {
            transform.localScale = Vector3.one * Mathf.Max(1f, _magnification);
        }

        /// <summary>Loads a case: geometry, appearance and laterality all change.</summary>
        public void LoadCase(GlaucomaCase glaucomaCase)
        {
            if (glaucomaCase == null)
            {
                OLog.Warn(LogCat.Anatomy, "LoadCase called with null; keeping the current disc.");
                return;
            }

            _case = glaucomaCase;
            _spec = glaucomaCase.Onh;
            _laterality = glaucomaCase.Laterality;

            // Drop any imported photograph. Leaving it would paint one patient's
            // fundus onto the next case's geometry, which is both wrong and, on a
            // teaching tool, actively misleading.
            _study = null;

            RebuildAll();
            OLog.Info(LogCat.Anatomy, "Loaded case: " + glaucomaCase.DisplayName);
        }

        /// <summary>Geometry and appearance. Use when the case changes.</summary>
        public void RebuildAll()
        {
            RebuildGeometry();
            RebuildAppearance();
        }

        /// <summary>
        /// Rebuilds the surface and mesh. Cheap enough for a slider drag.
        /// </summary>
        public void RebuildGeometry()
        {
            if (_builder == null) _builder = new OnhMeshBuilder(_sectors, _rings);
            if (_mesh == null) return;

            Surface = new OnhSurface(_spec);
            _builder.Build(_mesh, Surface, _laterality);

            Action<OnhModelBinding> handler = GeometryRebuilt;
            if (handler != null) handler(this);
        }

        /// <summary>
        /// Regenerates the fundus texture. EXPENSIVE - once per case, never per frame.
        /// </summary>
        public void RebuildAppearance()
        {
            if (Surface == null) Surface = new OnhSurface(_spec);

            EnsureMaterial();

            if (_fundus != null) Destroy(_fundus);
            _fundus = BuildFundusTexture();

            if (_fundus != null && _material != null)
            {
                if (_material.HasProperty("_BaseMap")) _material.SetTexture("_BaseMap", _fundus);
                else _material.mainTexture = _fundus;
            }
        }

        /// <summary>
        /// The photograph when there is a usable one, the procedural fundus
        /// otherwise. Falling back rather than failing is deliberate: an
        /// unreadable texture should cost the disc its photographic appearance, not
        /// leave an untextured grey blob and a scene that looks broken.
        /// </summary>
        private Texture2D BuildFundusTexture()
        {
            if (_study == null || _study.Photograph == null)
                return FundusTextureBuilder.Build(Surface, _textureResolution);

            FundusImage photo = FundusPhotoTexture.ToFundusImage(_study.Photograph);

            if (photo == null)
                return FundusTextureBuilder.Build(Surface, _textureResolution);

            return FundusPhotoTexture.Build(Surface, photo, _study.DiscCentrePixels,
                                            _study.BuildCalibration(), _laterality,
                                            _textureResolution);
        }

        /// <summary>
        /// Loads an imported eye: fits its traced margins to a spec, then builds the
        /// nerve head from that spec and paints the photograph onto it.
        ///
        /// <para>Note what is NOT happening. The fitted contours do not become the
        /// displayed measurements. The fit produces a spec, the spec produces a
        /// surface, and the measurement engine then reads the surface -- the same
        /// path an authored case takes. An image-derived CDR is therefore a
        /// measurement of geometry, exactly like every other number here, and a bad
        /// fit shows up as a residual instead of a confident wrong answer.</para>
        /// </summary>
        public OnhFitReport LoadStudy(FundusStudy study)
        {
            if (study == null)
            {
                OLog.Warn(LogCat.Anatomy, "LoadStudy called with null; keeping the current disc.");
                return null;
            }

            OnhFitReport report = study.Fit();

            if (report == null)
            {
                OLog.Warn(LogCat.Anatomy,
                    "Study '" + study.DisplayName + "' has no traced disc and cup margins yet.");
                return null;
            }

            // A transient case rather than none. The HUD reads its clinical context
            // and teaching note, and study.ApplyTo puts the fit's caveats there --
            // so "cup depth is a population estimate" lands in front of the reader
            // instead of staying in a report object nobody displays.
            GlaucomaCase transientCase = ScriptableObject.CreateInstance<GlaucomaCase>();
            study.ApplyTo(transientCase, report);

            _study = study;
            _case = transientCase;
            _spec = report.Spec;
            _laterality = study.Laterality;

            RebuildAll();

            OLog.Info(LogCat.Anatomy, "Loaded study: " + study.DisplayName + " -- " + report.Summary());

            return report;
        }

        /// <summary>Measures the current surface. Never reads the authored spec.</summary>
        public OnhMetrics Measure(MeasurementMethod method)
        {
            if (Surface == null) Surface = new OnhSurface(_spec);
            return OnhMeasurementEngine.Measure(Surface, method);
        }

        /// <summary>RNFL profile derived from the current geometry.</summary>
        public RnflProfile MeasureRnfl()
        {
            if (Surface == null) Surface = new OnhSurface(_spec);
            return RnflProfile.Measure(Surface);
        }

        /// <summary>Visual field derived from the current geometry, via the RNFL.</summary>
        public VisualFieldResult AnalyseField()
        {
            return VisualFieldModel.Analyse(MeasureRnfl(), _laterality);
        }

        /// <summary>
        /// Converts a point on the disc (mm, disc-space) into world space, so
        /// overlays and the planimetry tool can place markers on the surface.
        /// </summary>
        public Vector3 DiscPointToWorld(float radiusMm, float theta)
        {
            if (Surface == null) return transform.position;

            float h = Surface.HeightMm(radiusMm, theta);
            float xSign = _laterality.MirrorsX() ? -1f : 1f;

            Vector3 local = new Vector3(
                xSign * radiusMm * Mathf.Cos(theta) * OnhMeshBuilder.MmToUnits,
                h * OnhMeshBuilder.MmToUnits,
                radiusMm * Mathf.Sin(theta) * OnhMeshBuilder.MmToUnits);

            return transform.TransformPoint(local);
        }

        /// <summary>
        /// Inverse of DiscPointToWorld: a world point back to (radius mm, theta).
        /// The planimetry tool needs this to turn a click into a disc coordinate.
        /// </summary>
        public void WorldToDiscPoint(Vector3 world, out float radiusMm, out float theta)
        {
            Vector3 local = transform.InverseTransformPoint(world);

            float xSign = _laterality.MirrorsX() ? -1f : 1f;
            float x = xSign * local.x / OnhMeshBuilder.MmToUnits;
            float z = local.z / OnhMeshBuilder.MmToUnits;

            radiusMm = Mathf.Sqrt(x * x + z * z);
            theta = Mathf.Repeat(Mathf.Atan2(z, x), DiscGeometry.TwoPi);
        }
    }
}
