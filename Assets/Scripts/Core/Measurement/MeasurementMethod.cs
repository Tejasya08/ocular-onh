// -----------------------------------------------------------------------------
// Ocular -- how the cup margin is decided.
//
// There is no single "true" cup. Every instrument and every examiner draws the
// cup margin by a different rule, which is why two reports on the same nerve can
// quote different cup-to-disc ratios and both be correct. Offering the two rules
// side by side, over identical geometry, is the point of the exercise.
// -----------------------------------------------------------------------------

namespace Ocular.Core.Measurement
{
    public enum MeasurementMethod
    {
        /// <summary>
        /// Heidelberg-style reference plane. The plane sits a fixed 50 microns
        /// below the retinal surface height at the temporal disc margin; anything
        /// below the plane is cup, anything above it is rim.
        ///
        /// <para>Instrument-reproducible and the citable clinical algorithm, but it
        /// reads a slightly SMALLER cup than the geometric margin, because the cup
        /// wall has to descend 50 microns before the plane counts it. On a shallow
        /// cup it can find no cup at all. That gap is a teaching point, not a
        /// defect in either method.</para>
        /// </summary>
        ReferencePlane,

        /// <summary>
        /// Contour by wall slope. Scanning inward from the disc margin, the cup
        /// begins where the surface first tips steeply downward.
        ///
        /// <para>This is what an examiner judges at the slit lamp: the bend where
        /// the rim rolls over, seen stereoscopically, not a depth cut-off. It lands
        /// very close to the authored cup margin regardless of cup depth, which is
        /// exactly why a clinician and a machine disagree on shallow saucerised
        /// discs.</para>
        /// </summary>
        SlopeContour
    }
}
