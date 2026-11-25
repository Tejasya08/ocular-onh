// -----------------------------------------------------------------------------
// Ocular -- the desktop "hold it up and turn it over" camera.
//
// Uses the legacy Input class deliberately. The new Input System needs an
// InputActions asset and an activeInputHandler that permits it; the generator
// creates this scene in batch mode where neither is guaranteed, and a camera
// that silently stops responding is a very expensive way to find that out. The
// XR path goes through IPointerSource instead, so nothing here blocks a headset.
//
// Framing presets exist because the useful views of a nerve head are specific and
// awkward to find by hand: straight down the optic axis (what a fundus photo
// shows), an oblique that reveals the cup wall, and an edge-on section. A learner
// hunting for the section view with a mouse never quite gets there.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace Ocular.Interaction
{
    public enum FramingPreset
    {
        EnFace,
        Oblique,
        CrossSection
    }

    [RequireComponent(typeof(Camera))]
    public sealed class OrbitInspector : MonoBehaviour
    {
        [Header("Target")]
        [SerializeField] private Transform _target;

        [Header("Orbit")]
        [SerializeField] private float _orbitSpeed = 220f;
        [SerializeField] private float _panSpeed = 0.6f;
        [SerializeField] private float _zoomSpeed = 0.15f;

        [Header("Distance")]
        [SerializeField] private float _distance = 0.55f;
        [SerializeField] private float _minDistance = 0.12f;
        [SerializeField] private float _maxDistance = 3.0f;

        [Header("Smoothing")]
        [Tooltip("Seconds to settle. 0 disables smoothing entirely.")]
        [SerializeField] private float _smoothTime = 0.08f;

        private float _yaw;
        private float _pitch = 65f;
        private Vector3 _pivot;

        private float _targetYaw, _targetPitch, _targetDistance;
        private Vector3 _targetPivot;

        private Vector3 _pivotVelocity;

        public Transform Target { get { return _target; } set { _target = value; ResetToTarget(); } }

        private void Start()
        {
            ResetToTarget();
            Apply(true);
        }

        public void ResetToTarget()
        {
            _pivot = _target != null ? _target.position : Vector3.zero;
            _targetPivot = _pivot;
            _targetYaw = _yaw;
            _targetPitch = _pitch;
            _targetDistance = _distance;
        }

        /// <summary>
        /// Frames a named view. Pitch is measured from the horizon, so 89 is
        /// straight down; 90 exactly would make the up vector degenerate and the
        /// camera would roll unpredictably as it crossed the pole.
        /// </summary>
        public void Frame(FramingPreset preset)
        {
            switch (preset)
            {
                case FramingPreset.EnFace:
                    _targetPitch = 89f;
                    _targetYaw = 0f;
                    _targetDistance = 0.5f;
                    break;

                case FramingPreset.Oblique:
                    _targetPitch = 38f;
                    _targetYaw = 35f;
                    _targetDistance = 0.55f;
                    break;

                case FramingPreset.CrossSection:
                    // Just above the plane of the retina, looking across the cup.
                    _targetPitch = 6f;
                    _targetYaw = 90f;
                    _targetDistance = 0.42f;
                    break;
            }

            _targetPivot = _target != null ? _target.position : Vector3.zero;
        }

        private void Update()
        {
            // Never steal the mouse from a UI panel the user is dragging a slider on.
            bool overUi = UnityEngine.EventSystems.EventSystem.current != null
                       && UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject();

            if (!overUi)
            {
                if (Input.GetMouseButton(0))
                {
                    _targetYaw += Input.GetAxis("Mouse X") * _orbitSpeed * Time.unscaledDeltaTime;
                    _targetPitch -= Input.GetAxis("Mouse Y") * _orbitSpeed * Time.unscaledDeltaTime;

                    // Clamped short of the poles for the reason given in Frame().
                    _targetPitch = Mathf.Clamp(_targetPitch, -89f, 89f);
                }

                if (Input.GetMouseButton(2))
                {
                    Vector3 delta = transform.right * -Input.GetAxis("Mouse X")
                                  + transform.up * -Input.GetAxis("Mouse Y");

                    // Pan scaled by distance, so it feels the same close up and far out.
                    _targetPivot += delta * _panSpeed * _targetDistance * Time.unscaledDeltaTime * 60f;
                }

                float scroll = Input.GetAxis("Mouse ScrollWheel");
                if (Mathf.Abs(scroll) > 0.0001f)
                {
                    // Multiplicative, so each notch is the same proportional step
                    // whether you are at 12cm or 3m.
                    _targetDistance *= Mathf.Exp(-scroll * _zoomSpeed * 20f);
                    _targetDistance = Mathf.Clamp(_targetDistance, _minDistance, _maxDistance);
                }
            }

            if (Input.GetKeyDown(KeyCode.Alpha1)) Frame(FramingPreset.EnFace);
            if (Input.GetKeyDown(KeyCode.Alpha2)) Frame(FramingPreset.Oblique);
            if (Input.GetKeyDown(KeyCode.Alpha3)) Frame(FramingPreset.CrossSection);

            Apply(false);
        }

        private void Apply(bool instant)
        {
            if (instant || _smoothTime <= 0f)
            {
                _yaw = _targetYaw;
                _pitch = _targetPitch;
                _distance = _targetDistance;
                _pivot = _targetPivot;
            }
            else
            {
                // unscaledDeltaTime throughout: a paused or slowed simulation must
                // not freeze the user's ability to look at it.
                float t = 1f - Mathf.Exp(-Time.unscaledDeltaTime / _smoothTime);
                _yaw = Mathf.Lerp(_yaw, _targetYaw, t);
                _pitch = Mathf.Lerp(_pitch, _targetPitch, t);
                _distance = Mathf.Lerp(_distance, _targetDistance, t);
                _pivot = Vector3.SmoothDamp(_pivot, _targetPivot, ref _pivotVelocity, _smoothTime, Mathf.Infinity, Time.unscaledDeltaTime);
            }

            Quaternion rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            transform.rotation = rotation;
            transform.position = _pivot - rotation * Vector3.forward * _distance;
        }
    }
}
