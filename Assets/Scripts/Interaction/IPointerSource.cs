// -----------------------------------------------------------------------------
// Ocular -- the seam that makes desktop, VR and AR one code path.
//
// Everything that points at the nerve head -- planimetry, grabbing, the section
// meridian -- consumes this and nothing else. A mouse produces a ray from the
// camera; a tracked controller produces a ray from the controller. Neither the
// planimetry tool nor the overlays should ever know which one is driving them,
// because the moment they do, adding a headset means editing every one of them.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace Ocular.Interaction
{
    public interface IPointerSource
    {
        /// <summary>False when this source has no valid pose this frame (controller lost tracking, cursor off-screen).</summary>
        bool IsValid { get; }

        /// <summary>World-space ray the user is pointing along.</summary>
        Ray Ray { get; }

        /// <summary>Primary action: mouse button, or trigger. True on the frame it went down.</summary>
        bool SelectPressed { get; }

        /// <summary>Primary action held.</summary>
        bool SelectHeld { get; }

        /// <summary>Secondary action: right button, or grip. Used for grab and for undo.</summary>
        bool GripPressed { get; }
    }
}
