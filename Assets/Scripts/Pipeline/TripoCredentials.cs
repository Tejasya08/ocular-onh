// -----------------------------------------------------------------------------
// Ocular -- where the Tripo API key comes from at run time, and where it must
// never come from.
//
// IT IS NOT COMPILED INTO THE BUILD, and that is deliberate rather than
// fastidious. A key baked into a player is a key anyone who has the player can
// extract -- the strings are sitting in the assembly, and "it's just an .exe"
// stops a curious person for about a minute. Tripo keys spend real money from
// your balance, so a clinic copy of this simulator handed around on a USB stick
// would be handing around your credit card with it.
//
// So the key is looked up at run time, in this order:
//
//   1. The TRIPO_API_KEY environment variable.
//   2. A file named tripo_key.txt sitting NEXT TO the executable.
//   3. Whatever the user typed into the app, remembered in PlayerPrefs.
//
// All three keep the key out of the shipped files. Note that none of them make
// it secret FROM THE MACHINE'S OWN USER -- PlayerPrefs is the registry on
// Windows and the text file is plain text. That is the right threat model here:
// this protects you from distributing your key by accident, not from someone
// who is already sitting at the computer.
// -----------------------------------------------------------------------------

using System;
using System.IO;
using UnityEngine;
using Ocular.Core.Diagnostics;

namespace Ocular.Pipeline
{
    public static class TripoCredentials
    {
        public const string EnvironmentVariable = "TRIPO_API_KEY";

        /// <summary>File checked next to the executable. Keep it out of version control.</summary>
        public const string KeyFileName = "tripo_key.txt";

        private const string PlayerPrefsKey = "Ocular.TripoApiKey";

        /// <summary>Tripo keys are prefixed; used only to catch an obviously wrong paste.</summary>
        private const string ExpectedPrefix = "tsk_";

        /// <summary>
        /// The key, or null when no source has a plausible one.
        ///
        /// <para>A source whose value does not even look like a key is SKIPPED
        /// rather than used, and the next source gets its turn. This is not
        /// fussiness. The highest-priority source is an environment variable,
        /// which is exactly the one most likely to hold something stale, truncated
        /// or -- genuinely observed -- the literal placeholder out of a set-up
        /// instruction. Returning it would let seventeen characters of
        /// "tsk_your_key_here" silently outrank a perfectly good key file, and the
        /// only symptom is a 401 from a request the user cannot see.</para>
        ///
        /// <para>The check is shape only, so a real key that has been revoked
        /// still gets used and still fails at Tripo -- which is correct, because
        /// that failure is honest and this one was not.</para>
        /// </summary>
        public static string Resolve()
        {
            string fromEnvironment = Accept(SafeEnvironment(), "the " + EnvironmentVariable + " variable");
            if (fromEnvironment != null) return fromEnvironment;

            string fromFile = Accept(SafeFile(), KeyFileName);
            if (fromFile != null) return fromFile;

            string fromPrefs = Accept(PlayerPrefs.GetString(PlayerPrefsKey, string.Empty), "the saved key");
            if (fromPrefs != null) return fromPrefs;

            return null;
        }

        /// <summary>Returns a trimmed key, or null after saying why it was rejected.</summary>
        private static string Accept(string candidate, string sourceName)
        {
            if (string.IsNullOrEmpty(candidate)) return null;

            string trimmed = candidate.Trim();

            if (LooksValid(trimmed)) return trimmed;

            OLog.Warn(LogCat.Core, string.Format(
                "Ignoring {0}: '{1}' is not shaped like a Tripo key (expected {2}... and over "
                + "20 characters, got {3}). Looking at the next source.",
                sourceName, Describe(trimmed), ExpectedPrefix, trimmed.Length));

            return null;
        }

        public static bool HasKey { get { return !string.IsNullOrEmpty(Resolve()); } }

        /// <summary>Remembers a key the user typed into the app.</summary>
        public static void Save(string key)
        {
            if (key == null) key = string.Empty;

            PlayerPrefs.SetString(PlayerPrefsKey, key.Trim());
            PlayerPrefs.Save();
        }

        public static void Forget()
        {
            PlayerPrefs.DeleteKey(PlayerPrefsKey);
            PlayerPrefs.Save();
        }

        /// <summary>Shape check only. The real test is whether Tripo accepts it.</summary>
        public static bool LooksValid(string key)
        {
            return !string.IsNullOrEmpty(key)
                && key.Trim().StartsWith(ExpectedPrefix, StringComparison.Ordinal)
                && key.Trim().Length > 20;
        }

        /// <summary>Where the key file would go. Shown in the UI so it can be found.</summary>
        public static string KeyFilePath()
        {
            try
            {
                string beside = Path.GetDirectoryName(Path.GetFullPath(
                    Path.Combine(Application.dataPath, "..")));

                return Path.Combine(beside ?? ".", KeyFileName);
            }
            catch (ArgumentException)
            {
                return KeyFileName;
            }
        }

        /// <summary>
        /// Never echo the key itself into a log or a HUD. Four characters is
        /// enough to tell two keys apart when something is misconfigured and not
        /// enough to be worth anything over someone's shoulder.
        /// </summary>
        public static string Describe(string key)
        {
            if (string.IsNullOrEmpty(key)) return "none";

            string trimmed = key.Trim();

            return trimmed.Length <= 8
                ? "set"
                : trimmed.Substring(0, 4) + "..." + trimmed.Substring(trimmed.Length - 4);
        }

        private static string SafeEnvironment()
        {
            try
            {
                return Environment.GetEnvironmentVariable(EnvironmentVariable);
            }
            catch (Exception)
            {
                // Some platforms refuse environment access outright. Not an error;
                // there are two more sources to try.
                return null;
            }
        }

        private static string SafeFile()
        {
            try
            {
                string path = KeyFilePath();
                if (!File.Exists(path)) return null;

                string contents = File.ReadAllText(path).Trim();

                if (string.IsNullOrEmpty(contents)) return null;

                OLog.Info(LogCat.Core, "Tripo key loaded from " + path);
                return contents;
            }
            catch (Exception ex)
            {
                OLog.Warn(LogCat.Core, "Could not read the Tripo key file: " + ex.Message);
                return null;
            }
        }
    }
}
