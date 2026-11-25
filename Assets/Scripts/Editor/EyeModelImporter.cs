// -----------------------------------------------------------------------------
// Ocular -- makes a dropped eye model import correctly without being asked.
//
// The point of the drop folder is that dropping is the whole workflow. That only
// holds if the import settings are right on arrival, because the defaults are
// wrong here in ways that are quiet: a model arrives with its own built-in
// pipeline materials (magenta under URP), possibly with cameras, lights and
// animation clips attached, and at whatever scale its DCC tool felt like.
//
// EyeGlobeView already measures the model and overrides its materials at
// runtime, so none of this is load-bearing -- it is here so the asset in the
// Project window matches what actually renders, and so nobody spends an evening
// on a magenta eyeball.
// -----------------------------------------------------------------------------

using System.IO;
using UnityEditor;
using UnityEngine;
using Ocular.Anatomy;
using Ocular.Core.Diagnostics;

namespace Ocular.Editor
{
    public sealed class EyeModelImporter : AssetPostprocessor
    {
        /// <summary>Where a model has to land to be picked up automatically.</summary>
        public const string DropFolder = "Assets/Resources/" + EyeGlobeView.ResourceFolder;

        private bool IsEyeModel()
        {
            return assetPath.Replace('\\', '/').StartsWith(DropFolder + "/", System.StringComparison.OrdinalIgnoreCase);
        }

        private void OnPreprocessModel()
        {
            if (!IsEyeModel()) return;

            ModelImporter importer = (ModelImporter)assetImporter;

            // Materials are replaced at runtime, so importing the model's own only
            // creates assets that are never used and are magenta if anyone looks.
            importer.materialImportMode = ModelImporterMaterialImportMode.None;

            // A downloaded eye scene often carries a studio rig with it. Cameras
            // and lights instantiated into the scene would fight the one key light
            // the disc is deliberately lit by.
            importer.importCameras = false;
            importer.importLights = false;
            importer.importAnimation = false;
            importer.importBlendShapes = false;
            importer.importVisibility = false;

            // No collider. The orbit controller and the planimetry tool raycast at
            // the disc, and a mesh collider wrapped around the whole eye eats every
            // one of those rays -- the tools stop responding and nothing says why.
            importer.addCollider = false;

            // Scale is not trusted from here. EyeGlobeView measures the mesh and
            // fits it to a real eye, because an import scale is a claim about units
            // that a downloaded asset is in no position to make honestly.
            importer.globalScale = 1f;
            importer.useFileScale = false;

            importer.meshCompression = ModelImporterMeshCompression.Medium;
            importer.optimizeMeshVertices = true;
        }

        private void OnPostprocessModel(GameObject model)
        {
            if (!IsEyeModel()) return;

            int triangles = 0;
            MeshFilter[] filters = model.GetComponentsInChildren<MeshFilter>(true);

            for (int i = 0; i < filters.Length; i++)
                if (filters[i].sharedMesh != null) triangles += filters[i].sharedMesh.triangles.Length / 3;

            OLog.Info(LogCat.Build, string.Format(
                "Eye model '{0}' imported ({1:n0} triangles). It will appear around the disc on the "
                + "next run -- press G to cycle ghost / sectioned / solid / off.",
                model.name, triangles));

            // Integrated graphics and a WebGL target. Store-bought anatomical eyes
            // routinely ship north of 100k triangles, which is fine on a desktop
            // GPU and is not fine in a browser on an integrated one.
            if (triangles > 60000)
            {
                OLog.Warn(LogCat.Build, string.Format(
                    "'{0}' is {1:n0} triangles. It is scenery -- decimate it to about 20k before "
                    + "building for WebGL.", model.name, triangles));
            }
        }

        [MenuItem("Ocular/Eye Model/Open Drop Folder", priority = 40)]
        private static void OpenDropFolder()
        {
            string absolute = Path.GetFullPath(DropFolder);

            if (!Directory.Exists(absolute))
            {
                Directory.CreateDirectory(absolute);
                AssetDatabase.Refresh();
            }

            Object folder = AssetDatabase.LoadAssetAtPath<Object>(DropFolder);

            if (folder != null)
            {
                Selection.activeObject = folder;
                EditorGUIUtility.PingObject(folder);
            }

            EditorUtility.RevealInFinder(absolute);
        }

        [MenuItem("Ocular/Eye Model/What Is Installed", priority = 41)]
        private static void ReportInstalled()
        {
            GameObject[] models = Resources.LoadAll<GameObject>(EyeGlobeView.ResourceFolder);

            string message = models == null || models.Length == 0
                ? "No eye model installed.\n\nDrop an .fbx or .obj into\n" + DropFolder
                  + "\n\nIt is scaled and placed automatically; nothing else to set up."
                : "Installed eye models:\n\n" + string.Join("\n", System.Array.ConvertAll(models, m => "  " + m.name))
                  + (models.Length > 1 ? "\n\nOnly the first alphabetically is used." : string.Empty);

            EditorUtility.DisplayDialog("Ocular - Eye Model", message, "OK");
        }
    }
}
