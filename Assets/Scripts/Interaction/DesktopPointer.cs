// -----------------------------------------------------------------------------
// Ocular -- mouse implementation of IPointerSource.
//
// Also the fallback the XR path uses before a headset exists, which is what lets
// the VR interaction model be developed and tested on a laptop. If the only way
// to exercise the pointer code were to put a headset on, the pointer code would
// be the least-tested part of the project.
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEngine.EventSystems;

namespace Ocular.Interaction
{
    public sealed class DesktopPointer : MonoBehaviour, IPointerSource
    {
        [SerializeField] private Camera _camera;

        private void Awake()
        {
            if (_camera == null) _camera = GetComponent<Camera>();
            if (_camera == null) _camera = Camera.main;
        }

        /// <summary>
        /// Invalid while the cursor is over a UI panel, so a click that was meant
        /// for a button cannot also drop a contour point on the disc behind it.
        /// </summary>
        public bool IsValid
        {
            get
            {
                if (_camera == null) return false;

                Vector3 m = Input.mousePosition;
                if (m.x < 0f || m.y < 0f || m.x > Screen.width || m.y > Screen.height) return false;

                if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return false;

                return true;
            }
        }

        public Ray Ray
        {
            get
            {
                return _camera != null
                    ? _camera.ScreenPointToRay(Input.mousePosition)
                    : new Ray(Vector3.zero, Vector3.forward);
            }
        }

        // Button 1 rather than 0: left-drag is already the orbit gesture, and
        // stealing it would make placing a contour point fight the camera.
        public bool SelectPressed { get { return Input.GetMouseButtonDown(1); } }
        public bool SelectHeld { get { return Input.GetMouseButton(1); } }
        public bool GripPressed { get { return Input.GetKeyDown(KeyCode.Backspace); } }
    }
}
