// -----------------------------------------------------------------------------
// Ocular -- let the clinician draw the margins, then mark their work.
//
// This is what turns the project from a viewer into a training tool. The app can
// already measure the disc perfectly; that teaches nobody anything. Here the user
// commits to a disc margin and a cup margin the way they would at a slit lamp,
// and only then sees the truth.
//
// The score is reported SIGNED, not as a magnitude. "You were 0.12 out" hides the
// thing worth knowing; "you over-called the cup by 0.12" is the actual feedback,
// and systematic over-calling of large cups is the specific error this whole case
// set was built to expose.
//
// Points are placed by intersecting the pointer ray with the RETINAL PLANE rather
// than raycasting the mesh. That avoids a MeshCollider whose cooked mesh would
// have to be rebuilt every time a slider moves -- and it matches what the user is
// actually judging, which is the margin's position seen face-on, not the depth of
// the tissue under their cursor.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using UnityEngine;
using Ocular.Anatomy;
using Ocular.Core.Anatomy;
using Ocular.Core.Measurement;

namespace Ocular.Interaction
{
    public enum PlanimetryStage
    {
        Idle,
        DrawingDisc,
        DrawingCup,
        Complete
    }

    public struct PlanimetryResult
    {
        public float UserVerticalCdr;
        public float UserHorizontalCdr;
        public float UserAreaCdr;

        public float TruthVerticalCdr;
        public float TruthHorizontalCdr;
        public float TruthAreaCdr;

        /// <summary>User minus truth. Positive means the cup was over-called.</summary>
        public float VerticalErrorSigned;

        public float UserDiscAreaMm2;
        public float UserCupAreaMm2;
        public float UserRimAreaMm2;

        public int DiscPointCount;
        public int CupPointCount;
    }

    public sealed class PlanimetryTool : MonoBehaviour
    {
        /// <summary>Fewer points than this cannot describe a closed margin.</summary>
        public const int MinimumPoints = 5;

        [SerializeField] private OnhModelBinding _model;
        [SerializeField] private MonoBehaviour _pointerBehaviour;   // must implement IPointerSource

        private IPointerSource _pointer;

        private readonly List<Vector2> _discPoints = new List<Vector2>();
        private readonly List<Vector2> _cupPoints = new List<Vector2>();

        public PlanimetryStage Stage { get; private set; }
        public PlanimetryResult LastResult { get; private set; }

        public IList<Vector2> DiscPoints { get { return _discPoints; } }
        public IList<Vector2> CupPoints { get { return _cupPoints; } }

        /// <summary>Raised whenever the contours change, for live overlay redraw.</summary>
        public event Action<PlanimetryTool> Changed;

        /// <summary>Raised once both contours are closed and scored.</summary>
        public event Action<PlanimetryResult> Completed;

        private void Awake()
        {
            _pointer = _pointerBehaviour as IPointerSource;

            if (_pointer == null && _pointerBehaviour != null)
            {
                Debug.LogError("[Interaction] Assigned pointer does not implement IPointerSource.");
            }
        }

        public void Begin()
        {
            _discPoints.Clear();
            _cupPoints.Clear();
            Stage = PlanimetryStage.DrawingDisc;
            Raise();
        }

        public void Clear()
        {
            _discPoints.Clear();
            _cupPoints.Clear();
            Stage = PlanimetryStage.Idle;
            Raise();
        }

        /// <summary>Closes the current contour and moves on, scoring when both are done.</summary>
        public void NextContour()
        {
            if (Stage == PlanimetryStage.DrawingDisc)
            {
                if (_discPoints.Count < MinimumPoints) return;
                Stage = PlanimetryStage.DrawingCup;
                Raise();
                return;
            }

            if (Stage == PlanimetryStage.DrawingCup)
            {
                if (_cupPoints.Count < MinimumPoints) return;
                Stage = PlanimetryStage.Complete;
                Score();
            }
        }

        public void Undo()
        {
            List<Vector2> active = ActiveList();
            if (active == null || active.Count == 0) return;

            active.RemoveAt(active.Count - 1);
            Raise();
        }

        private List<Vector2> ActiveList()
        {
            if (Stage == PlanimetryStage.DrawingDisc) return _discPoints;
            if (Stage == PlanimetryStage.DrawingCup) return _cupPoints;
            return null;
        }

        private void Update()
        {
            if (_pointer == null || _model == null) return;
            if (Stage != PlanimetryStage.DrawingDisc && Stage != PlanimetryStage.DrawingCup) return;
            if (!_pointer.IsValid) return;

            if (_pointer.GripPressed) Undo();

            if (_pointer.SelectPressed)
            {
                Vector2 mm;
                if (TryProjectToRetinalPlane(_pointer.Ray, out mm))
                {
                    ActiveList().Add(mm);
                    Raise();
                }
            }

            if (Input.GetKeyDown(KeyCode.Return)) NextContour();
        }

        /// <summary>
        /// Intersects a world ray with the disc's own retinal plane and returns the
        /// hit in disc-space millimetres (x temporal-positive, y superior-positive).
        /// </summary>
        public bool TryProjectToRetinalPlane(Ray worldRay, out Vector2 discMm)
        {
            discMm = Vector2.zero;
            if (_model == null) return false;

            Transform t = _model.transform;

            Vector3 origin = t.InverseTransformPoint(worldRay.origin);
            Vector3 dir = t.InverseTransformDirection(worldRay.direction);

            // Looking along the retinal plane rather than at it: no usable
            // intersection, and dividing by dir.y would fling the point to infinity.
            if (Mathf.Abs(dir.y) < 1e-5f) return false;

            float distance = -origin.y / dir.y;
            if (distance <= 0f) return false;   // plane is behind the viewer

            Vector3 local = origin + dir * distance;

            float xSign = _model.Laterality.MirrorsX() ? -1f : 1f;

            // Local units back to millimetres, and out of the mirrored presentation
            // frame into disc-space, so every stored point is directly comparable
            // with what the measurement engine reports.
            discMm = new Vector2(
                xSign * local.x / OnhMeshBuilder.MmToUnits,
                local.z / OnhMeshBuilder.MmToUnits);

            return true;
        }

        private void Score()
        {
            PlanimetryResult r = new PlanimetryResult();

            r.DiscPointCount = _discPoints.Count;
            r.CupPointCount = _cupPoints.Count;

            float discV, discH, discArea;
            float cupV, cupH, cupArea;

            Measure(_discPoints, out discV, out discH, out discArea);
            Measure(_cupPoints, out cupV, out cupH, out cupArea);

            r.UserDiscAreaMm2 = discArea;
            r.UserCupAreaMm2 = cupArea;
            r.UserRimAreaMm2 = Mathf.Max(0f, discArea - cupArea);

            r.UserVerticalCdr = discV > 1e-4f ? Mathf.Clamp01(cupV / discV) : 0f;
            r.UserHorizontalCdr = discH > 1e-4f ? Mathf.Clamp01(cupH / discH) : 0f;
            r.UserAreaCdr = discArea > 1e-6f ? Mathf.Clamp01(cupArea / discArea) : 0f;

            // Scored against the slope-contour method: it is the one that
            // approximates what a clinician judges at the slit lamp, so marking a
            // human estimate against the reference plane would penalise them for a
            // 50 micron instrument convention they were never using.
            OnhMetrics truth = OnhMeasurementEngine.Measure(_model.Surface, MeasurementMethod.SlopeContour);

            r.TruthVerticalCdr = truth.VerticalCdr;
            r.TruthHorizontalCdr = truth.HorizontalCdr;
            r.TruthAreaCdr = truth.AreaCdr;

            r.VerticalErrorSigned = r.UserVerticalCdr - r.TruthVerticalCdr;

            LastResult = r;

            Raise();

            Action<PlanimetryResult> handler = Completed;
            if (handler != null) handler(r);
        }

        /// <summary>
        /// Vertical and horizontal extent, and enclosed area, of a hand-drawn
        /// contour in disc-space millimetres.
        /// </summary>
        private static void Measure(List<Vector2> points, out float verticalMm,
                                    out float horizontalMm, out float areaMm2)
        {
            verticalMm = 0f;
            horizontalMm = 0f;
            areaMm2 = 0f;

            if (points == null || points.Count < 3) return;

            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;

            for (int i = 0; i < points.Count; i++)
            {
                Vector2 p = points[i];
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.y < minY) minY = p.y;
                if (p.y > maxY) maxY = p.y;
            }

            horizontalMm = maxX - minX;
            verticalMm = maxY - minY;

            // Shoelace over the points ORDERED BY ANGLE about their centroid. Users
            // do not click a margin in a tidy sweep; taking them in click order
            // produces a self-intersecting star whose signed area is meaningless.
            Vector2 centre = Vector2.zero;
            for (int i = 0; i < points.Count; i++) centre += points[i];
            centre /= points.Count;

            List<Vector2> ordered = new List<Vector2>(points);
            ordered.Sort((a, b) =>
                Mathf.Atan2(a.y - centre.y, a.x - centre.x)
                .CompareTo(Mathf.Atan2(b.y - centre.y, b.x - centre.x)));

            float sum = 0f;
            for (int i = 0; i < ordered.Count; i++)
            {
                Vector2 p = ordered[i];
                Vector2 q = ordered[(i + 1) % ordered.Count];
                sum += p.x * q.y - q.x * p.y;
            }

            areaMm2 = Mathf.Abs(sum) * 0.5f;
        }

        private void Raise()
        {
            Action<PlanimetryTool> handler = Changed;
            if (handler != null) handler(this);
        }
    }
}
