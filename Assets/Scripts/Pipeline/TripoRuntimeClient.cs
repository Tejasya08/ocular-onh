// -----------------------------------------------------------------------------
// Ocular -- the Tripo v3 API, from inside the running simulator.
//
// Mirrors Tools/ocular_pipeline.py, which is the reference implementation and
// stays the one to read first. This exists because the Python version can only
// put a model into the project at BUILD time, and the clinician wants a model
// while the program is open.
//
// GLB, NOT FBX, and that is forced by the runtime. Unity's FBX and OBJ
// importers are EDITOR-ONLY -- nothing in a player can read an .fbx. glTF is
// the only common format with a runtime loader (glTFast), and GLB is its
// single-file binary flavour with the textures embedded, so one download is the
// whole model. The offline pipeline asks for quad=true to force FBX; this one
// must not, which also happens to be 5 credits cheaper (30 vs 35) because the
// quad add-on is no longer being paid for.
//
// Errors arrive as HTTP 200 with a non-zero `code` in the body, so every
// response is checked on `code` and never on the status line alone.
// -----------------------------------------------------------------------------

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using Ocular.Core.Diagnostics;

namespace Ocular.Pipeline
{
    /// <summary>Raised for any Tripo-side failure. The message is safe to show a user.</summary>
    public sealed class TripoException : Exception
    {
        public int Code { get; private set; }

        public TripoException(string message, int code = 0) : base(message) { Code = code; }
    }

    public sealed class TripoRuntimeClient
    {
        public const string BaseUrl = "https://openapi.tripo3d.ai/v3";

        /// <summary>Geometry model. Matches the offline pipeline.</summary>
        public const string ModelVersion = "v3.1-20260211";

        /// <summary>
        /// What one eye costs without the quad add-on: image-to-3D with standard
        /// texture. The runtime path cannot use quad (it forces FBX, which no
        /// player can load), so it is the cheaper of the two.
        /// </summary>
        public const int CreditsPerEye = 30;

        private const int PollMilliseconds = 3000;
        private const int TimeoutSeconds = 900;

        private readonly string _apiKey;

        public TripoRuntimeClient(string apiKey)
        {
            if (string.IsNullOrEmpty(apiKey))
                throw new TripoException("No Tripo API key.");

            _apiKey = apiKey.Trim();
        }

        /// <summary>Available credits, or -1 when the balance cannot be read.</summary>
        public async Task<float> GetBalanceAsync(CancellationToken ct)
        {
            try
            {
                string body = await GetAsync(BaseUrl + "/account/balance", ct);
                BalanceEnvelope parsed = JsonUtility.FromJson<BalanceEnvelope>(body);

                return parsed != null && parsed.data != null ? parsed.data.balance : -1f;
            }
            catch (TripoException)
            {
                return -1f;
            }
        }

        /// <summary>
        /// The whole job: upload, generate, poll, download. Returns the GLB bytes.
        /// <paramref name="onProgress"/> receives a label and 0..1.
        /// </summary>
        public async Task<byte[]> GenerateModelAsync(byte[] imageBytes, string fileName,
                                                     Action<string, float> onProgress,
                                                     CancellationToken ct)
        {
            Report(onProgress, "Checking credit", 0f);

            float balance = await GetBalanceAsync(ct);

            if (balance >= 0f && balance < CreditsPerEye)
            {
                throw new TripoException(string.Format(
                    "Tripo balance is {0:0} credits; one eye costs {1}. Top up at "
                    + "developers.tripo3d.ai/en/pricing. A Tripo subscription does not "
                    + "include API credit.", balance, CreditsPerEye));
            }

            Report(onProgress, "Uploading image", 0.05f);
            string fileToken = await UploadAsync(imageBytes, fileName, ct);

            Report(onProgress, "Starting generation", 0.1f);
            string taskId = await CreateTaskAsync(fileToken, ct);

            string modelUrl = await PollAsync(taskId, onProgress, ct);

            Report(onProgress, "Downloading model", 0.9f);
            byte[] model = await DownloadAsync(modelUrl, ct);

            Report(onProgress, "Model ready", 1f);
            return model;
        }

        // ------------------------------------------------------------------

        private async Task<string> UploadAsync(byte[] imageBytes, string fileName, CancellationToken ct)
        {
            WWWForm form = new WWWForm();
            form.AddBinaryData("file", imageBytes, fileName, MimeFor(fileName));

            using (UnityWebRequest request = UnityWebRequest.Post(BaseUrl + "/files", form))
            {
                request.SetRequestHeader("Authorization", "Bearer " + _apiKey);

                string body = await SendAsync(request, ct);

                UploadEnvelope parsed = JsonUtility.FromJson<UploadEnvelope>(body);

                if (parsed == null || parsed.data == null || string.IsNullOrEmpty(parsed.data.file_token))
                    throw new TripoException("Upload returned no file_token.");

                return parsed.data.file_token;
            }
        }

        private async Task<string> CreateTaskAsync(string fileToken, CancellationToken ct)
        {
            // Hand-built rather than JsonUtility, which cannot serialise a plain
            // object without a matching type and would need a class per request.
            string json = "{\"input\":\"" + Escape(fileToken) + "\","
                        + "\"model\":\"" + ModelVersion + "\","
                        + "\"texture\":true,\"pbr\":true,"
                        + "\"face_limit\":20000}";

            using (UnityWebRequest request = new UnityWebRequest(
                       BaseUrl + "/generation/image-to-model", "POST"))
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", "Bearer " + _apiKey);

                string body = await SendAsync(request, ct);

                TaskEnvelope parsed = JsonUtility.FromJson<TaskEnvelope>(body);

                if (parsed == null || parsed.data == null || string.IsNullOrEmpty(parsed.data.task_id))
                    throw new TripoException("Task creation returned no task_id.");

                return parsed.data.task_id;
            }
        }

        private async Task<string> PollAsync(string taskId, Action<string, float> onProgress,
                                             CancellationToken ct)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(TimeoutSeconds);

            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                string body = await GetAsync(BaseUrl + "/tasks/" + taskId, ct);
                StatusEnvelope parsed = JsonUtility.FromJson<StatusEnvelope>(body);

                if (parsed == null || parsed.data == null)
                    throw new TripoException("Task query returned nothing usable.");

                StatusData data = parsed.data;

                if (data.status == "success")
                {
                    if (data.output == null || string.IsNullOrEmpty(data.output.model_url))
                        throw new TripoException("Task succeeded with no model_url.");

                    return data.output.model_url;
                }

                if (data.status == "failed" || data.status == "cancelled")
                {
                    throw new TripoException("Generation " + data.status + ": "
                        + (string.IsNullOrEmpty(data.error_message) ? "no detail" : data.error_message),
                        data.error_code);
                }

                // 10%..85% of the overall bar belongs to generation.
                Report(onProgress, "Generating 3D model", 0.1f + 0.75f * (data.progress / 100f));

                await Task.Delay(PollMilliseconds, ct);
            }

            throw new TripoException("Generation did not finish within "
                + TimeoutSeconds + " seconds.");
        }

        private async Task<byte[]> DownloadAsync(string url, CancellationToken ct)
        {
            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                // The CDN link is pre-signed and must NOT carry the Authorization
                // header: some object stores reject a request that is signed twice.
                await SendAsync(request, ct, checkCode: false);

                return request.downloadHandler.data;
            }
        }

        private async Task<string> GetAsync(string url, CancellationToken ct)
        {
            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                request.SetRequestHeader("Authorization", "Bearer " + _apiKey);
                return await SendAsync(request, ct);
            }
        }

        /// <summary>
        /// Sends and waits without blocking the main thread, then checks Tripo's
        /// body-level `code`. Polling IsDone with Task.Yield keeps this usable
        /// from ordinary async code while Unity keeps rendering.
        /// </summary>
        private async Task<string> SendAsync(UnityWebRequest request, CancellationToken ct,
                                             bool checkCode = true)
        {
            UnityWebRequestAsyncOperation operation = request.SendWebRequest();

            while (!operation.isDone)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            if (request.result != UnityWebRequest.Result.Success)
            {
                throw new TripoException(request.error + " (" + request.url + ")");
            }

            string body = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;

            if (checkCode) ThrowOnCode(body);

            return body;
        }

        /// <summary>
        /// Tripo signals application errors with HTTP 200 and a non-zero `code`,
        /// so a request that "succeeded" can still have failed.
        /// </summary>
        private static void ThrowOnCode(string body)
        {
            if (string.IsNullOrEmpty(body)) return;

            ErrorEnvelope parsed = null;

            try { parsed = JsonUtility.FromJson<ErrorEnvelope>(body); }
            catch (ArgumentException) { return; }   // not JSON we recognise

            if (parsed == null || parsed.code == 0) return;

            string message = string.IsNullOrEmpty(parsed.message)
                ? "Tripo error " + parsed.code
                : parsed.message;

            if (!string.IsNullOrEmpty(parsed.suggestion)) message += " -- " + parsed.suggestion;

            throw new TripoException(message, parsed.code);
        }

        private static void Report(Action<string, float> onProgress, string label, float fraction)
        {
            if (onProgress != null) onProgress(label, Mathf.Clamp01(fraction));
        }

        private static string MimeFor(string fileName)
        {
            string lower = fileName.ToLowerInvariant();

            if (lower.EndsWith(".png")) return "image/png";
            if (lower.EndsWith(".webp")) return "image/webp";

            return "image/jpeg";
        }

        private static string Escape(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        // --- Response shapes. JsonUtility needs concrete serialisable types. ---

        [Serializable] private class ErrorEnvelope { public int code; public string message; public string suggestion; }
        [Serializable] private class UploadEnvelope { public int code; public UploadData data; }
        [Serializable] private class UploadData { public string file_token; }
        [Serializable] private class TaskEnvelope { public int code; public TaskData data; }
        [Serializable] private class TaskData { public string task_id; }
        [Serializable] private class BalanceEnvelope { public int code; public BalanceData data; }
        [Serializable] private class BalanceData { public float balance; public float frozen; }
        [Serializable] private class StatusEnvelope { public int code; public StatusData data; }

        [Serializable]
        private class StatusData
        {
            public string task_id;
            public string status;
            public int progress;
            public int error_code;
            public string error_message;
            public float credits_consumed;
            public OutputData output;
        }

        [Serializable] private class OutputData { public string model_url; public string rendered_image_url; }
    }
}
