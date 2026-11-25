// -----------------------------------------------------------------------------
// Ocular -- tracked-controller implementation of IPointerSource.
//
// COMPILES WITH OR WITHOUT the WebXR package. Everything package-dependent sits
// behind OCULAR_WEBXR, which Ocular.Interaction.asmdef defines automatically via
// versionDefines when com.de-panther.webxr is present -- no manual scripting
// define to forget, and no broken project for anyone who clones this without the
// package.
//
// Without the package it still WORKS, driven by the mouse. That is deliberate:
// if the only way to exercise the VR pointer were to put a headset on, the VR
// pointer would be the least-tested code in the project, and it would break
// silently every time something upstream changed.
// -----------------------------------------------------------------------------

using UnityEngine;

#if OCULAR_WEBXR
using WebXR;
#endif

namespace Ocular.Interaction
{
    public enum XrHand
    {
        Left,
        Right
    }

    public sealed class XrPointer : MonoBehaviour, IPointerSource
    {
        [SerializeField] private XrHand _hand = XrHand.Right;

        [Tooltip("Used when no headset session is active, so the VR interaction model is testable on the desktop.")]
        [SerializeField] private DesktopPointer _desktopFallback;

        /// <summary>True when a real XR session is driving this pointer.</summary>
        public bool IsTracking { get; private set; }

#if OCULAR_WEBXR
        private WebXRController _controller;

        private void Awake()
        {
            _controller = GetComponent<WebXRController>();
        }

        private void Update()
        {
            // A controller that has not reported a pose yet would otherwise point
            // from the origin along +Z and drop planimetry points in the middle of
            // the disc on the first frame of every session.
            IsTracking = _controller != null && _controller.isControllerActive;
        }

        public bool IsValid
        {
            get { return IsTracking || (_desktopFallback != null && _desktopFallback.IsValid); }
        }

        public Ray Ray
        {
            get
            {
                if (IsTracking) return new Ray(transform.position, transform.forward);
                return _desktopFallback != null ? _desktopFallback.Ray : new Ray(Vector3.zero, Vector3.forward);
            }
        }

        public bool SelectPressed
        {
            get
            {
                if (IsTracking) return _controller.GetButtonDown(WebXRController.ButtonTypes.Trigger);
                return _desktopFallback != null && _desktopFallback.SelectPressed;
            }
        }

        public bool SelectHeld
        {
            get
            {
                if (IsTracking) return _controller.GetButton(WebXRController.ButtonTypes.Trigger);
                return _desktopFallback != null && _desktopFallback.SelectHeld;
            }
        }

        public bool GripPressed
        {
            get
            {
                if (IsTracking) return _controller.GetButtonDown(WebXRController.ButtonTypes.Grip);
                return _desktopFallback != null && _desktopFallback.GripPressed;
            }
        }
#else
        // No WebXR package: pass everything through to the mouse. The VR code path
        // above is the only thing missing, and the interaction model is identical.
        private void Update() { IsTracking = false; }

        public bool IsValid { get { return _desktopFallback != null && _desktopFallback.IsValid; } }
        public Ray Ray { get { return _desktopFallback != null ? _desktopFallback.Ray : new Ray(Vector3.zero, Vector3.forward); } }
        public bool SelectPressed { get { return _desktopFallback != null && _desktopFallback.SelectPressed; } }
        public bool SelectHeld { get { return _desktopFallback != null && _desktopFallback.SelectHeld; } }
        public bool GripPressed { get { return _desktopFallback != null && _desktopFallback.GripPressed; } }
#endif

        public XrHand Hand { get { return _hand; } }
    }
}
