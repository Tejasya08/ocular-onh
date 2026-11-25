// -----------------------------------------------------------------------------
// Ocular -- "choose an image" in a built player.
//
// Unity ships no file dialog for players. EditorUtility.OpenFilePanel exists
// only in the Editor, and a built .exe that calls it does not compile. So on
// Windows this calls the OS dialog directly through comdlg32, which is the same
// dialog every other Windows program opens and needs no plugin or package.
//
// Everything is wrapped so that a failure degrades to "no file chosen" rather
// than taking the simulator down. A P/Invoke that throws inside a UI button
// handler would otherwise leave the HUD dead with no visible cause.
//
// WebGL cannot do any of this -- a browser only hands over a file through an
// <input type="file"> element the user clicked, which needs a .jslib shim and a
// different code path. Supported() reports false there so the UI can say so
// instead of showing a button that does nothing.
// -----------------------------------------------------------------------------

using System;
using System.Runtime.InteropServices;
using UnityEngine;
using Ocular.Core.Diagnostics;

namespace Ocular.Pipeline
{
    public static class RuntimeFileDialog
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OpenFileName
        {
            public int structSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public string filter;
            public string customFilter;
            public int maxCustFilter;
            public int filterIndex;
            public IntPtr file;
            public int maxFile;
            public IntPtr fileTitle;
            public int maxFileTitle;
            public string initialDir;
            public string title;
            public int flags;
            public short fileOffset;
            public short fileExtension;
            public string defExt;
            public IntPtr custData;
            public IntPtr hook;
            public string templateName;
            public IntPtr reservedPtr;
            public int reservedInt;
            public int flagsEx;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetOpenFileNameW(ref OpenFileName ofn);

        private const int OfnFileMustExist = 0x00001000;
        private const int OfnPathMustExist = 0x00000800;
        private const int OfnNoChangeDir = 0x00000008;
        private const int MaxPath = 1024;

        public static bool Supported() { return true; }

        /// <summary>Opens the OS picker. Returns null if cancelled or unavailable.</summary>
        public static string PickImage(string title)
        {
            IntPtr buffer = IntPtr.Zero;

            try
            {
                buffer = Marshal.AllocCoTaskMem(MaxPath * sizeof(char));

                // Zero it: the dialog reads this as the initial filename and a
                // buffer full of whatever was on the heap shows as garbage, or
                // trips the length check and fails with no message.
                for (int i = 0; i < MaxPath; i++) Marshal.WriteInt16(buffer, i * 2, 0);

                OpenFileName ofn = new OpenFileName();
                ofn.structSize = Marshal.SizeOf(typeof(OpenFileName));
                ofn.file = buffer;
                ofn.maxFile = MaxPath;

                // Each entry is a label and a pattern, every one NUL-terminated,
                // and the whole list terminated by a second NUL. Miss the final
                // one and the dialog shows no file types at all.
                ofn.filter = "Fundus images\0*.png;*.jpg;*.jpeg\0All files\0*.*\0\0";
                ofn.filterIndex = 1;

                ofn.title = title;
                ofn.flags = OfnFileMustExist | OfnPathMustExist | OfnNoChangeDir;

                // NoChangeDir matters more than it looks: without it the dialog
                // leaves the process's working directory wherever the user last
                // browsed, and every later relative path in the app resolves
                // somewhere unexpected.

                if (!GetOpenFileNameW(ref ofn)) return null;   // cancelled

                return Marshal.PtrToStringUni(buffer);
            }
            catch (Exception ex)
            {
                OLog.Warn(LogCat.UI, "File dialog failed: " + ex.Message);
                return null;
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeCoTaskMem(buffer);
            }
        }

#else

        public static bool Supported() { return false; }

        public static string PickImage(string title)
        {
            OLog.Warn(LogCat.UI,
                "No runtime file dialog on this platform. Put the image in the Inbox "
                + "folder and use Tools/ocular_watch.py instead.");
            return null;
        }

#endif
    }
}
