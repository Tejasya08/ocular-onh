// -----------------------------------------------------------------------------
// Ocular -- entering and leaving VR and AR, and reshaping the scene for each.
//
// The three modes are not the same scene at different scales:
//
//   DESKTOP  the disc is a specimen on a turntable, magnified ~120x, orbited
//            with the mouse. The camera moves, the model does not.
//   VR       the disc is an object in the room at arm's length. The USER moves;
//            the orbit camera must be switched OFF or it fights the headset pose
//            and induces motion sickness immediately.
//   AR       the disc is sitting in the user's actual room, so it has to be
//            SMALLER (a 21cm eye floating over a desk reads as a prop, not a
//            model) and the camera must clear to TRANSPARENT or the passthrough
//            feed is painted over with a solid background and "AR" looks exactly
//            like VR with extra steps.
//
// State is polled rather than event-subscribed. Reading one enum per frame is
// free, and it avoids binding to the package's delegate signatures, which are
// the part most likely to change between WebXR Export versions.
// -----------------------------------------------------------------------------

using System;
using UnityEngine;
using Ocular.Anatomy;
using Ocular.Core.Diagnostics;

#if OCULAR_WEBXR
using WebXR;
#endif

namespace Ocular.Interaction
{
    public enum OcularViewMode
    {
        Desktop,
        VR,
        AR
    }

    public sealed class XrSessionController : MonoBehaviour
    {
        [SerializeField] private OnhModelBinding _model;
        [SerializeField] private Camera _camera;
        [SerializeField] private OrbitInspector _orbit;

        [Header("Presentation per mode")]
        [Tooltip("Desktop magnification. A 1.8mm disc becomes about 21cm.")]
        [SerializeField] private float _desktopMagnification = 120f;

        [Tooltip("VR magnification. Slightly smaller so it sits comfortably at arm's length.")]
        [SerializeField] private float _vrMagnification = 90f;

        [Tooltip("AR magnification. Smaller again so it reads as an object on a real surface.")]
        [SerializeField] private float _arMagnification = 45f;

        [SerializeField] private Vector3 _vrModelPosition = new Vector3(0f, 1.25f, 0.55f);
        [SerializeField] private Vector3 _arModelPosition = new Vector3(0f, 1.0f, 0.5f);

        private OcularViewMode _mode = OcularViewMode.Desktop;
        private CameraClearFlags _savedClearFlags;
        private Color _savedBackground;
        private Vector3 _savedModelPosition;
        private bool _saved;

        public OcularViewMode Mode { get { return _mode; } }

        /// <summary>Raised when the session mode actually changes.</summary>
        public event Action<XrSessionController> ModeChanged;

#if OCULAR_WEBXR
        public bool IsVrSupported
        {
            get { return WebXRManager.Instance != null && WebXRManager.Instance.isSupportedVR; }
        }

        public bool IsArSupported
        {
            get { return WebXRManager.Instance != null && WebXRManager.Instance.isSupportedAR; }
        }

        public bool IsPackagePresent { get { return true; } }

        /// <summary>
        /// Must be called from a real user gesture (a UI button click). Browsers
        /// refuse to start an XR session from anything else, and the refusal is
        /// silent, so a "press V for VR" shortcut would simply appear broken.
        /// </summary>
        public void EnterVr()
        {
            if (WebXRManager.Instance == null) return;

            if (!IsVrSupported)
            {
                OLog.Warn(LogCat.Interaction, "VR not supported by this browser/device.");
                return;
            }

            WebXRManager.Instance.ToggleVR();
        }

        public void EnterAr()
        {
            if (WebXRManager.Instance == null) return;

            if (!IsArSupported)
            {
                OLog.Warn(LogCat.Interaction, "AR not supported by this browser/device.");
                return;
            }

            WebXRManager.Instance.ToggleAR();
        }

        private void Update()
        {
            if (WebXRManager.Instance == null) return;

            OcularViewMode next;
            switch (WebXRManager.Instance.XRState)
            {
                case WebXRState.VR: next = OcularViewMode.VR; break;
                case WebXRState.AR: next = OcularViewMode.AR; break;
                default: next = OcularViewMode.Desktop; break;
            }

            if (next != _mode) ApplyMode(next);
        }
#else
        // No package: the mode machinery still exists and still reshapes the scene,
        // so the desktop path is identical and the VR/AR buttons can explain
        // themselves rather than silently doing nothing.
        public bool IsVrSupported { get { return false; } }
        public bool IsArSupported { get { return false; } }
        public bool IsPackagePresent { get { return false; } }

        public void EnterVr()
        {
            OLog.Warn(LogCat.Interaction, "WebXR package not installed; VR unavailable.");
        }

        public void EnterAr()
        {
            OLog.Warn(LogCat.Interaction, "WebXR package not installed; AR unavailable.");
        }
#endif

        private void Awake()
        {
            if (_camera == null) _camera = Camera.main;
            SaveDesktopState();
        }

        private void SaveDesktopState()
        {
            if (_saved || _camera == null) return;

            _savedClearFlags = _camera.clearFlags;
            _savedBackground = _camera.backgroundColor;
            _savedModelPosition = _model != null ? _model.transform.position : Vector3.zero;
            _saved = true;
        }

        private void ApplyMode(OcularViewMode next)
        {
            SaveDesktopState();
            _mode = next;

            switch (next)
            {
                case OcularViewMode.Desktop: ApplyDesktop(); break;
                case OcularViewMode.VR: ApplyVr(); break;
                case OcularViewMode.AR: ApplyAr(); break;
            }

            OLog.Info(LogCat.Interaction, "View mode: " + next);

            Action<XrSessionController> handler = ModeChanged;
            if (handler != null) handler(this);
        }

        private void ApplyDesktop()
        {
            if (_orbit != null) _orbit.enabled = true;

            if (_camera != null)
            {
                _camera.clearFlags = _savedClearFlags;
                _camera.backgroundColor = _savedBackground;
            }

            if (_model != null)
            {
                _model.Magnification = _desktopMagnification;
                _model.transform.position = _savedModelPosition;
            }
        }

        private void ApplyVr()
        {
            // Off, not merely ignored. Leaving it enabled means the script keeps
            // writing camera.position every frame while the headset also writes it,
            // and the resulting fight is the single fastest way to make someone
            // motion sick.
            if (_orbit != null) _orbit.enabled = false;

            if (_model != null)
            {
                _model.Magnification = _vrMagnification;
                _model.transform.position = _vrModelPosition;
            }
        }

        private void ApplyAr()
        {
            if (_orbit != null) _orbit.enabled = false;

            if (_camera != null)
            {
                // Alpha zero is the part that matters: the browser composites the
                // camera feed behind whatever Unity leaves transparent. A solid
                // background here would hide the real world completely.
                _camera.clearFlags = CameraClearFlags.SolidColor;
                _camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            }

            if (_model != null)
            {
                _model.Magnification = _arMagnification;
                _model.transform.position = _arModelPosition;
            }
        }

        public string ModeLabel
        {
            get
            {
                if (!IsPackagePresent) return "Desktop (WebXR package not installed)";

                switch (_mode)
                {
                    case OcularViewMode.VR: return "VR session active";
                    case OcularViewMode.AR: return "AR session active";
                    default: return "Desktop";
                }
            }
        }
    }
}
