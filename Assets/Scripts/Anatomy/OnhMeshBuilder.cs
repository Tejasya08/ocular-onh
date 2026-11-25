// -----------------------------------------------------------------------------
// Ocular -- tessellating the nerve head height field into a mesh.
//
// THE ONLY PLACE MILLIMETRES BECOME UNITY UNITS. Ocular.Core is millimetres from
// end to end and knows nothing about world space; everything above this line is
// metres. Keeping the conversion to a single constant in a single file is what
// stops a stray factor of a thousand turning up in a measurement six months from
// now -- and the measurement engine never sees Unity units at all, so a bug in
// this file can make the eye the wrong size but can never make the reported CDR
// wrong.
//
// The mesh is a polar grid. That matches how the surface is defined (radius and
// meridian), so ring lines fall exactly along contours of constant radius and the
// cup margin lands on a ring boundary instead of being smeared across quads.
//
// Rebuilt every frame while a slider is dragged, so the arrays are retained and
// refilled rather than reallocated. A fresh 24k-vertex Vector3[] per frame is
// several megabytes a second of garbage, which on WebGL is a visible hitch.
// -----------------------------------------------------------------------------

using UnityEngine;
using Ocular.Core.Anatomy;

namespace Ocular.Anatomy
{
    public sealed class OnhMeshBuilder
    {
        /// <summary>Millimetres to Unity units. 1 unit = 1 metre, so the eye is life-size.</summary>
        public const float MmToUnits = 0.001f;

        public const int DefaultSectors = 128;
        public const int DefaultRings = 96;

        private readonly int _sectors;
        private readonly int _rings;

        private Vector3[] _vertices;
        private Vector3[] _normals;
        private Vector2[] _uvs;
        private Color32[] _colors;
        private int[] _triangles;

        // Rings are distributed by a power curve rather than uniformly: the cup
        // wall and the rim occupy the inner half of the radius but carry nearly all
        // the curvature, so an even spread wastes rings on flat retina and leaves
        // the wall faceted.
        private const float RingBias = 0.65f;

        public OnhMeshBuilder(int sectors = DefaultSectors, int rings = DefaultRings)
        {
            _sectors = Mathf.Max(8, sectors);
            _rings = Mathf.Max(4, rings);
            Allocate();
        }

        public int VertexCount { get { return (_rings + 1) * (_sectors + 1); } }

        private void Allocate()
        {
            int verts = VertexCount;
            _vertices = new Vector3[verts];
            _normals = new Vector3[verts];
            _uvs = new Vector2[verts];
            _colors = new Color32[verts];
            _triangles = new int[_rings * _sectors * 6];

            BuildTriangles();
        }

        /// <summary>
        /// Index buffer is topology only, so it is built once. The seam column
        /// (sector == _sectors) duplicates the first column's position but carries
        /// u = 1 instead of u = 0, which is what stops the fundus texture smearing
        /// the whole width of the image across the last quad.
        /// </summary>
        private void BuildTriangles()
        {
            int t = 0;
            int stride = _sectors + 1;

            for (int ring = 0; ring < _rings; ring++)
            {
                for (int s = 0; s < _sectors; s++)
                {
                    int a = ring * stride + s;
                    int b = a + 1;
                    int c = a + stride;
                    int d = c + 1;

                    _triangles[t++] = a;
                    _triangles[t++] = c;
                    _triangles[t++] = b;

                    _triangles[t++] = b;
                    _triangles[t++] = c;
                    _triangles[t++] = d;
                }
            }
        }

        /// <summary>
        /// Fills <paramref name="mesh"/> from the surface. Pass the same Mesh back in
        /// on every rebuild.
        /// </summary>
        public void Build(Mesh mesh, OnhSurface surface, Laterality laterality)
        {
            if (mesh == null || surface == null) return;

            float extentMm = surface.RetinaExtentMm;

            // OS is the mirror image of OD in world space. Doing it here, by
            // negating X once, is what lets every measurement stay in disc-space.
            float xSign = laterality.MirrorsX() ? -1f : 1f;

            int stride = _sectors + 1;

            for (int s = 0; s <= _sectors; s++)
            {
                float u = (float)s / _sectors;
                float theta = u * DiscGeometry.TwoPi;

                float cos = Mathf.Cos(theta);
                float sin = Mathf.Sin(theta);

                // Hoisted out of the ring loop: these are the four trig-heavy calls
                // and they depend only on the meridian.
                float discR = surface.DiscRadiusMm(theta);
                float cupR = surface.CupRadiusMm(theta);

                for (int ring = 0; ring <= _rings; ring++)
                {
                    float tRing = (float)ring / _rings;
                    float rMm = Mathf.Pow(tRing, 1f / RingBias) * extentMm;

                    float hMm = surface.HeightMm(rMm, theta, discR, cupR);

                    int i = ring * stride + s;

                    _vertices[i] = new Vector3(
                        xSign * rMm * cos * MmToUnits,
                        hMm * MmToUnits,
                        rMm * sin * MmToUnits);

                    // Polar UVs, matching FundusTextureBuilder's own parameterisation
                    // so the texture lines up with the geometry it was generated from.
                    _uvs[i] = new Vector2(u, Mathf.Clamp01(rMm / extentMm));

                    _colors[i] = TissueColour(surface, rMm, theta, discR, cupR);
                }
            }

            mesh.Clear();
            mesh.indexFormat = VertexCount > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;

            mesh.vertices = _vertices;
            mesh.uv = _uvs;
            mesh.colors32 = _colors;
            mesh.triangles = _triangles;

            // Cheaper and more robust than hand-deriving normals from the analytic
            // gradient, and this runs at interactive rates for a mesh this size.
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            // Mirroring X flips triangle winding, so the surface would face away and
            // be culled. Recalculating after the flip is not enough - the winding is
            // in the index buffer - so the fix is to flip the normals instead.
            if (xSign < 0f)
            {
                mesh.GetNormals(_normalsList);
                for (int i = 0; i < _normalsList.Count; i++) _normalsList[i] = -_normalsList[i];
                mesh.SetNormals(_normalsList);
            }

            mesh.MarkDynamic();
        }

        private static readonly System.Collections.Generic.List<Vector3> _normalsList =
            new System.Collections.Generic.List<Vector3>();

        /// <summary>
        /// Vertex colour by tissue type. Not used for shading directly -- the fundus
        /// texture does that -- but it keeps the tissue classification on the mesh
        /// for overlays and for anyone who later wants a vertex-colour shader.
        /// </summary>
        private static Color32 TissueColour(OnhSurface surface, float rMm, float theta,
                                            float discR, float cupR)
        {
            if (rMm >= discR)
            {
                float ppa = surface.PpaFactor(rMm, theta, discR);
                return Color32.Lerp(FundusPalette.Retina32, FundusPalette.Ppa32, ppa);
            }

            if (rMm >= cupR) return FundusPalette.Rim32;
            return FundusPalette.CupFloor32;
        }
    }
}
