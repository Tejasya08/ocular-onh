// -----------------------------------------------------------------------------
// Ocular -- loads the Tripo API key from the web server at startup.
//
// On WebGL the file system and environment variables are both unavailable,
// so the normal TripoCredentials resolution chain falls through to PlayerPrefs.
// This component bridges the gap: it fetches tripo_key.txt from the same
// origin as the page (where the GitHub Actions workflow wrote it), trims it,
// and saves it to PlayerPrefs so TripoCredentials.Resolve() finds it.
//
// It runs once in Start and is otherwise silent. A missing or empty file is
// not an error -- it just means the key was not deployed, and the user will
// be asked for it when they try to load an image.
// -----------------------------------------------------------------------------

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using Ocular.Core.Diagnostics;

namespace Ocular.Pipeline
{
    public sealed class TripoKeyLoader : MonoBehaviour
    {
        private void Start()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            StartCoroutine(FetchKeyCoroutine());
#endif
        }

        private IEnumerator FetchKeyCoroutine()
        {
            // tripo_key.txt is deployed beside index.html by the GitHub Actions
            // workflow, so it lives at the same origin as the page.
            string url = GetBaseUrl() + "tripo_key.txt";

            OLog.Info(LogCat.Core, "Fetching Tripo key from " + url);

            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                request.timeout = 10;
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    OLog.Warn(LogCat.Core,
                        "Could not fetch tripo_key.txt: " + request.error
                        + " -- set the key via PlayerPrefs or TRIPO_API_KEY env var.");
                    yield break;
                }

                string key = request.downloadHandler.text.Trim();

                if (string.IsNullOrEmpty(key))
                {
                    OLog.Warn(LogCat.Core, "tripo_key.txt was empty.");
                    yield break;
                }

                if (!TripoCredentials.LooksValid(key))
                {
                    OLog.Warn(LogCat.Core,
                        "tripo_key.txt does not look like a valid Tripo key (expected tsk_...). "
                        + "Check the TRIPO_API_KEY secret in GitHub Actions.");
                    yield break;
                }

                // Save to PlayerPrefs so TripoCredentials.Resolve() finds it
                // for the lifetime of this session.
                TripoCredentials.Save(key);

                OLog.Info(LogCat.Core,
                    "Tripo key loaded from server: " + TripoCredentials.Describe(key));
            }
        }

        /// <summary>
        /// The page's base URL — everything up to and including the last slash.
        /// e.g. https://tejasya08.github.io/ocular-onh/ 
        /// Used to build the absolute URL for tripo_key.txt.
        /// </summary>
        private static string GetBaseUrl()
        {
            string url = Application.absoluteURL;

            if (string.IsNullOrEmpty(url)) return "./";

            // Strip query string and fragment
            int q = url.IndexOf('?');
            if (q >= 0) url = url.Substring(0, q);

            int h = url.IndexOf('#');
            if (h >= 0) url = url.Substring(0, h);

            // Ensure trailing slash
            int lastSlash = url.LastIndexOf('/');
            if (lastSlash >= 0) url = url.Substring(0, lastSlash + 1);

            return url;
        }
    }
}
