// -----------------------------------------------------------------------------
// Ocular -- fundus colours.
//
// One place, because these get used by both the mesh's vertex colours and the
// generated texture and the two must agree. Values are eyeballed from typical
// fundus photography of a lightly pigmented fundus; real fundus colour varies
// enormously with racial background and camera white balance, which is itself
// worth knowing -- "pallor" is judged against the rest of THAT eye, never
// against a remembered absolute.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace Ocular.Anatomy
{
    public static class FundusPalette
    {
        public static readonly Color Retina = new Color(0.78f, 0.35f, 0.19f);
        public static readonly Color RetinaDark = new Color(0.62f, 0.26f, 0.14f);

        /// <summary>Beta-zone PPA: a window onto sclera, so pale and slightly yellow.</summary>
        public static readonly Color Ppa = new Color(0.84f, 0.74f, 0.58f);

        /// <summary>Neuroretinal rim: healthy rim keeps an orange-pink flush.</summary>
        public static readonly Color Rim = new Color(0.88f, 0.58f, 0.45f);

        /// <summary>Cup floor: pale, exposing lamina cribrosa.</summary>
        public static readonly Color CupFloor = new Color(0.92f, 0.88f, 0.82f);

        /// <summary>Lamina cribrosa pores, visible in a deep cup.</summary>
        public static readonly Color Lamina = new Color(0.70f, 0.66f, 0.62f);

        public static readonly Color Vessel = new Color(0.48f, 0.12f, 0.12f);
        public static readonly Color VesselArtery = new Color(0.68f, 0.20f, 0.16f);

        /// <summary>Splinter (Drance) haemorrhage.</summary>
        public static readonly Color Haemorrhage = new Color(0.42f, 0.05f, 0.07f);

        public static readonly Color32 Retina32 = Retina;
        public static readonly Color32 Ppa32 = Ppa;
        public static readonly Color32 Rim32 = Rim;
        public static readonly Color32 CupFloor32 = CupFloor;
    }
}
