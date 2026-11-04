using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Wrenlet
{
    // ElevenLabs, a voice in the cloud, for reading quick voice answers with your own API key.
    // Each sentence is one request (text in, 16-bit PCM out), so it plays while the next one is
    // made, just like the natural voice. The key is kept encrypted for your Windows user (DPAPI)
    // in settings.json, and only ever sent to api.elevenlabs.io.
    static class ElevenLabs
    {
        const string Api = "https://api.elevenlabs.io";
        const string Model = "eleven_flash_v2_5"; // the fastest model, made for talking back
        const int SampleRate = 24000;             // pcm_24000 works on every plan
        public const string DefaultVoice = "JBFqnCBsd6RMkjVDRZzb"; // George, one of the voices every account has

        // The -5..5 reading speed. ElevenLabs takes 0.7 to 1.2.
        public static float Speed(int rate)
        {
            return (float)Math.Max(0.7, Math.Min(1.2, 1.05 + rate * 0.05));
        }

        // Speaks one sentence. previous is the sentence before it, so the two join up naturally.
        public static float[] Generate(string key, string voiceId, string text, string previous, float speed, out int sampleRate)
        {
            var body = new Dictionary<string, object>
            {
                { "text", text },
                { "model_id", Model },
                { "voice_settings", new Dictionary<string, object> { { "stability", 0.5 }, { "similarity_boost", 0.75 }, { "speed", Math.Round(speed, 2) } } },
            };
            if (!string.IsNullOrEmpty(previous)) body["previous_text"] = previous;
            string voice = string.IsNullOrEmpty(voiceId) ? DefaultVoice : voiceId;
            var req = Request(key, "/v1/text-to-speech/" + Uri.EscapeDataString(voice) + "?output_format=pcm_" + SampleRate, "POST");
            byte[] json = Encoding.UTF8.GetBytes(Json.Write(body));
            req.ContentType = "application/json";
            req.ContentLength = json.Length;
            using (var s = req.GetRequestStream()) s.Write(json, 0, json.Length);
            byte[] pcm = Send(req);
            var samples = new float[pcm.Length / 2];
            for (int i = 0; i < samples.Length; i++) samples[i] = (short)(pcm[2 * i] | (pcm[2 * i + 1] << 8)) / 32768f;
            sampleRate = SampleRate;
            return samples;
        }

        // The voices on the account (its own and the premade ones), as id and name. Also checks the key.
        public static List<KeyValuePair<string, string>> Voices(string key)
        {
            var list = new List<KeyValuePair<string, string>>();
            var d = Json.ParseObject(Encoding.UTF8.GetString(Send(Request(key, "/v2/voices?page_size=100&sort=name&sort_direction=asc", "GET"))));
            object voices;
            if (d != null && d.TryGetValue("voices", out voices) && voices is System.Collections.IList)
                foreach (object v in (System.Collections.IList)voices)
                {
                    var voice = v as Dictionary<string, object>;
                    string id = Json.Str(voice, "voice_id"), name = Json.Str(voice, "name");
                    if (id != "") list.Add(new KeyValuePair<string, string>(id, name != "" ? name : id));
                }
            return list;
        }

        static HttpWebRequest Request(string key, string path, string method)
        {
            if (string.IsNullOrEmpty(key)) throw new IOException("No ElevenLabs API key.");
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(Api + path);
            req.Method = method;
            req.Headers["xi-api-key"] = key;
            req.UserAgent = "Wrenlet";
            req.Timeout = 20000;
            req.ReadWriteTimeout = 20000;
            return req;
        }

        static byte[] Send(HttpWebRequest req)
        {
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var s = resp.GetResponseStream())
                using (var mem = new MemoryStream())
                {
                    s.CopyTo(mem);
                    return mem.ToArray();
                }
            }
            catch (WebException e)
            {
                var resp = e.Response as HttpWebResponse;
                if (resp == null) throw new IOException("Couldn't reach ElevenLabs (" + e.Status + ").");
                string detail = "";
                try { using (var r = new StreamReader(resp.GetResponseStream())) detail = Detail(r.ReadToEnd()); } catch { }
                int code = (int)resp.StatusCode;
                string what = code == 401 ? "The API key isn't valid" + (detail != "" ? " (" + detail + ")" : "")
                    : code == 402 || code == 429 ? "ElevenLabs refused: " + (detail != "" ? detail : "out of credits or too many requests")
                    : code == 404 ? "That voice isn't on your ElevenLabs account"
                    : "ElevenLabs answered " + code + (detail != "" ? ": " + detail : "");
                throw new IOException(what + ".");
            }
        }

        // The message in an ElevenLabs error ({"detail": {"message": ...}} or {"detail": "..."}).
        static string Detail(string json)
        {
            try
            {
                var d = Json.ParseObject(json);
                var inner = Json.Obj(d, "detail");
                string s = inner != null ? Json.Str(inner, "message") : Json.Str(d, "detail");
                return s.TrimEnd('.');
            }
            catch { return ""; }
        }

        // ---- the key, encrypted for this Windows user ------------------------

        public static string Seal(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser));
        }

        public static string Unseal(string sealedKey)
        {
            if (string.IsNullOrEmpty(sealedKey)) return "";
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(sealedKey), Entropy, DataProtectionScope.CurrentUser)); }
            catch { return ""; } // another user's or another PC's settings: the key has to be entered again
        }

        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ClaudeNotch ElevenLabs key");
    }
}
