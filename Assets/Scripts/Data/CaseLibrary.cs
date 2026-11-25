// -----------------------------------------------------------------------------
// Ocular -- the set of cases the simulation offers.
//
// ONE ScriptableObject type in this file. See GlaucomaCase.cs for why that rule
// is not stylistic.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace Ocular.Data
{
    [CreateAssetMenu(fileName = "CaseLibrary", menuName = "Ocular/Case Library")]
    public sealed class CaseLibrary : ScriptableObject
    {
        public List<GlaucomaCase> Cases = new List<GlaucomaCase>();

        public int Count { get { return Cases == null ? 0 : Cases.Count; } }

        /// <summary>
        /// Case at an index, wrapping out of range rather than throwing. The UI
        /// steps through this list with next/previous buttons and a library that is
        /// empty or short should not be able to break the viewer.
        /// </summary>
        public GlaucomaCase At(int index)
        {
            if (Cases == null || Cases.Count == 0) return null;
            int i = ((index % Cases.Count) + Cases.Count) % Cases.Count;
            return Cases[i];
        }
    }
}
