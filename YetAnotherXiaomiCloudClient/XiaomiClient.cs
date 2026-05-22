using Flurl;
using Flurl.Http;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace YetAnotherXiaomiCloudClient
{
    public class Weight
    {
        public DateTime Date { get; set; }
        public float WeightKg { get; set; }
        public float BMI { get; set; }
        public float BodyFat { get; set; }
        public float BodyWater { get; set; }
        public float BoneMass { get; set; }
        public int MetabolicAge { get; set; }
        public float MuscleMass { get; set; }
        public float ProteinMass { get; set; }
        public int VisceralFat { get; set; }
        public int BasalMetabolism { get; set; }
        public int BodyScore { get; set; }
        public int HeartRate { get; set; }
        public float SkeletalMuscleMass { get; set; }
        public string Source { get; set; }
        public string User { get; set; }
    }

    public class LoginResult
    {
        [JsonPropertyName("ssecurity")]
        public byte[] Ssecurity { get; set; }

        [JsonPropertyName("passToken")]
        public string PassToken { get; set; }

        [JsonPropertyName("userId")]
        public long UserId { get; set; }

        [JsonPropertyName("location")]
        public string Location { get; set; }

        [JsonPropertyName("result")]
        public string Result { get; set; }
    }

    public class XiaomiClient
    {
        private readonly HttpClient _http;
        public string Sid { get; }
        public string Cookies { get; set; }
        public long UserId { get; set; }
        public byte[] Ssecurity { get; set; }
        public string PassToken { get; set; }
        private CookieJar _cookiesJar { get; set; }

        public XiaomiClient(string app)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromMinutes(1) };
            Sid = app;
        }

        // LoginWithToken helpers
        // token format: "{userId}:{passToken}" (e.g. "123456:abcdef...")
        public void LoginWithToken(string token)
        {
            if (string.IsNullOrEmpty(token)) throw new ArgumentException("token is empty", nameof(token));

            var idx = token.IndexOf(':');
            if (idx <= 0 || idx == token.Length - 1) throw new ArgumentException("token must be in format '{userId}:{passToken}'", nameof(token));

            var uidPart = token.Substring(0, idx);
            var pass = token.Substring(idx + 1);

            if (!long.TryParse(uidPart, out var uid)) throw new ArgumentException("invalid userId in token", nameof(token));

            LoginWithToken(uid, pass);
        }

        public async Task LoginWithToken(long userId, string passToken)
        {
            if (string.IsNullOrEmpty(passToken)) throw new ArgumentException("passToken is empty", nameof(passToken));


            var loginResultRaw = await "https://account.xiaomi.com/pass/serviceLogin?_json=true&sid=xiaomiio"
                .WithHeader("User-Agent", "iqmevrwsojypkevwmr-DACACBDADADCC APP/com.xiaomi.mihome APPV/10.5.201")
                .WithHeader("Content-Type", "application/x-www-form-urlencoded")
                .WithHeader("Cookie", $"userId={userId}; passToken={passToken}")
                .WithCookies(out var jar)
                .GetAsync()
                .ReceiveString();

            var skippedStartString = loginResultRaw.Substring(11);

            var loginResult = JsonSerializer.Deserialize<LoginResult>(skippedStartString);


            UserId = userId;
            PassToken = passToken;
            _cookiesJar = jar;
            Ssecurity = loginResult.Ssecurity;
            // set cookies header value that will be reused by RequestAsync
            // Cookies = $"userId={userId}; passToken={passToken}";
            // keep using the existing HttpClient instance (_http) for requests

            await ServiceLogin3Async(loginResult.Location);
        }

        // Completes login by following the provided location URL and collecting Set-Cookie headers.
        // This mirrors the Go serviceLogin3 behaviour: perform GET to location and append all Set-Cookie
        // values (only the name=value part before the ";") into the Cookies property so RequestAsync
        // reuses them.
        public async Task ServiceLogin3Async(string location)
        {
            if (string.IsNullOrEmpty(location)) throw new ArgumentException("location is empty", nameof(location));

            var res = await _http.GetAsync(location);
            // Ensure response content is consumed so underlying connections can be reused
            _ = await res.Content.ReadAsStringAsync();

            if (res.Headers.TryGetValues("Set-Cookie", out var cookies))
            {
                foreach (var s in cookies)
                {
                    var cookiePair = s.Split(';', 2)[0].Trim();
                    if (string.IsNullOrEmpty(cookiePair)) continue;
                    if (!string.IsNullOrEmpty(Cookies)) Cookies += "; ";
                    Cookies += cookiePair;
                }
            }
        }

        public async Task<List<Weight>> GetModelWeights2(string region = "", string model ="")
        {
            var weights = new List<Weight>();

            var ts = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds();
            string parametersTemplate = "{\"endTime\":1,\"beginTime\":%begintime,\"model\":\"%model\",\"uid\":\"%userId\",\"did\":0,\"accountId\":0}";

            string parameters = parametersTemplate.Replace("%begintime", ts.ToString()).Replace("%model", model).Replace("%userId", UserId.ToString());

            while (true)
            {
                var baseUrl = MiFitnessURL(region);
                byte[]? data;
                try
                {
                    data = await RequestAsync(baseUrl, "/eco/common/scale/getUserDataByPage", parameters, new Dictionary<string, string> { { "Miot-Request-Model", model } });
                }
                catch (Exception ex)
                {

                    throw;
                }
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (!root.TryGetProperty("data_list", out var dataList))
                    throw new Exception("unexpected response: data_list missing");

                bool hasMore = false;
                string nextKey = null;
                if (root.TryGetProperty("has_more", out var hm) && hm.GetBoolean()) hasMore = true;
                if (root.TryGetProperty("next_key", out var nk) && nk.ValueKind == JsonValueKind.String) nextKey = nk.GetString();



                foreach (var item in dataList.EnumerateArray())
                {
                    var key = item.GetProperty("key").GetString();
                    if (key != "weight") continue;

                    var sid = item.GetProperty("sid").GetString();
                    var value = item.GetProperty("value").GetString();

                    // value is a JSON string
                    Weight w = ParseWeightFromValue(value);
                    w.Source = sid;
                    weights.Add(w);
                }

                if (!hasMore) break;

                parameters = string.Format("{\"start_time\":1,\"end_time\":%d,\"key\":\"weight\",\"next_key\":%q}", ts, nextKey);
                // above is a close approximation of go's fmt with %q. Instead create properly:
                parameters = "{" + $"\"start_time\":1,\"end_time\":{ts},\"key\":\"weight\",\"next_key\":\"{nextKey}\"" + "}";
            }

            return weights;
        }

        public async Task<List<Weight>> GetModelWeights(string region, string model)
        {
            var weights = new List<Weight>();

            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            switch (region)
            {
                case "":
                case "cn":
                    while (ts > 0)
                    {
                        var parameters = $"{{\"param\":{{\"endTime\":1,\"beginTime\":{ts}}},\"model\":\"{model}\",\"uid\":{UserId},\"did\":0}}";
                        var data = await RequestAsync(
                            "https://api.io.mi.com/app", "/eco/scale/getData", parameters,
                            new Dictionary<string, string> { { "MIOT-REQUEST-MODEL", model } }
                        );
                        ts = UnmarshalScaleData(data, weights);
                    }
                    break;

                case "de":
                case "i2":
                case "ru":
                case "sg":
                case "us":
                    while (ts > 0)
                    {
                        var parameters = $"{{\"endTime\":1,\"beginTime\":{ts},\"model\":\"{model}\",\"uid\":\"{UserId}\",\"did\":0,\"accountId\":0}}";
                        var data = await RequestAsync(
                            $"https://{region}.api.io.mi.com/app", "/eco/common/scale/getUserDataByPage", parameters,
                            new Dictionary<string, string> { { "MIOT-REQUEST-MODEL", model } }
                        );
                        ts = UnmarshalScaleData(data, weights);
                    }
                    break;

                default:
                    throw new ArgumentException($"xiaomi: unsupported region: {region}");
            }

            return weights;
        }

        private static Weight ParseWeightFromValue(string value)
        {
            try
            {
                using var vdoc = JsonDocument.Parse(value);
                var root = vdoc.RootElement;
                var w = new Weight();

                if (root.TryGetProperty("time", out var timeEl) && timeEl.ValueKind == JsonValueKind.Number)
                {
                    var t = timeEl.GetInt64();
                    w.Date = DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime;
                }
                else
                {
                    w.Date = DateTime.UtcNow;
                }

                w.WeightKg = GetFloat(root, "weight");
                w.BMI = GetFloat(root, "bmi");
                w.BodyFat = GetFloat(root, "body_fat_rate");
                w.BodyWater = GetFloat(root, "moisture_rate");
                w.BoneMass = GetFloat(root, "bone_mass");
                w.MetabolicAge = GetInt(root, "body_age");
                w.MuscleMass = GetFloat(root, "muscle_mass");
                w.ProteinMass = GetFloat(root, "protein_mass");
                w.VisceralFat = GetInt(root, "visceral_fat");
                w.BasalMetabolism = GetInt(root, "basal_metabolism");
                w.BodyScore = GetInt(root, "body_score");
                w.HeartRate = GetInt(root, "bpm");
                w.SkeletalMuscleMass = GetFloat(root, "skeletal_muscle_mass");

                return w;
            }
            catch
            {
                return new Weight { Date = DateTime.UtcNow };
            }
        }

        private static float GetFloat(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var p)) return 0f;
            switch (p.ValueKind)
            {
                case JsonValueKind.Number:
                    return p.GetSingle();
                case JsonValueKind.String:
                    if (float.TryParse(p.GetString(), out var f)) return f;
                    return 0f;
                default:
                    return 0f;
            }
        }

        private static int GetInt(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var p)) return 0;
            switch (p.ValueKind)
            {
                case JsonValueKind.Number:
                    return p.GetInt32();
                case JsonValueKind.String:
                    if (int.TryParse(p.GetString(), out var i)) return i;
                    return 0;
                default:
                    return 0;
            }
        }

        private static string MiFitnessURL(string region)
        {
            switch (region)
            {
                case "":
                case "cn":
                    return "https://api.io.mi.com/app";
                case "de":
                case "i2":
                case "ru":
                case "sg":
                case "us":
                    return "https://" + region + ".api.io.mi.com/app";
            }
            return string.Empty;
        }

        public async Task<byte[]> RequestAsync(string baseUrl, string apiUrl, string parameters, Dictionary<string, string> headers)
        {
            var values = new Dictionary<string, string>
            {
                ["data"] = parameters
            };

            var nonce = GenNonce();

            var signedNonce = GenSignedNonce(Ssecurity, nonce);

            // 1. gen hash for data param using plain data
            values["rc4_hash__"] = GenSignature64("POST", apiUrl, values, signedNonce);

            // 2. encrypt data and hash params
            var keys = new List<string>(values.Keys);
            foreach (var k in keys)
            {
                var plaintext = Encoding.UTF8.GetBytes(values[k]);
                var ciphertext = RC4Crypt(signedNonce, plaintext);
                values[k] = Convert.ToBase64String(ciphertext);
            }

            // 3. add signature for encrypted data and hash params
            values["signature"] = GenSignature64("POST", apiUrl, values, signedNonce);

            // 4. add nonce
            values["_nonce"] = Convert.ToBase64String(nonce);

            var url = baseUrl + apiUrl;

            var requestContent = new
            {
                data = values["data"],
                rc4_hash__ = values["rc4_hash__"],
                signature = values["signature"],
                _nonce = values["_nonce"],
            };

            var rawResult = await url
               .WithHeader("User-Agent", "iqmevrwsojypkevwmr-DACACBDADADCC APP/com.xiaomi.mihome APPV/10.5.201")
               .WithHeader("Content-Type", "application/x-www-form-urlencoded")
               .WithHeader("Cookie", Cookies)
               .WithHeaders(headers)
               .PostUrlEncodedAsync(requestContent)
               .ReceiveString();

            // response is base64 encoded ciphertext
            var ciphertextResp = Convert.FromBase64String(rawResult);
            var plaintextResp = RC4Crypt(signedNonce, ciphertextResp);

            // plaintextResp is JSON like {"code":0,"message":"ok","result":...}
            using var doc = JsonDocument.Parse(plaintextResp);
            var root = doc.RootElement;
            var code = root.GetProperty("code").GetInt32();
            if (code != 0)
            {
                var message = root.GetProperty("message").GetString();
                throw new Exception("xiaomi: " + message);
            }

            var result = root.GetProperty("result").GetRawText();
            return Encoding.UTF8.GetBytes(result);
        }

        private static byte[] GenNonce()
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
            var nonce = new byte[12];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(nonce.AsSpan(0, 8));
            // put uint32 big endian of ts into nonce[8..11]
            nonce[8] = (byte)((ts >> 24) & 0xFF);
            nonce[9] = (byte)((ts >> 16) & 0xFF);
            nonce[10] = (byte)((ts >> 8) & 0xFF);
            nonce[11] = (byte)(ts & 0xFF);
            return nonce;
        }

        private static byte[] GenSignedNonce(byte[] ssecurity, byte[] nonce)
        {
            using var sha256 = SHA256.Create();
            if (ssecurity != null) sha256.TransformBlock(ssecurity, 0, ssecurity.Length, null, 0);
            sha256.TransformFinalBlock(nonce, 0, nonce.Length);
            return sha256.Hash;
        }

        private static byte[] RC4Crypt(byte[] key, byte[] data)
        {
            // RC4 with drop 1024
            var s = new byte[256];
            for (int i = 0; i < 256; i++) s[i] = (byte)i;
            int j = 0;
            for (int i = 0; i < 256; i++)
            {
                j = (j + s[i] + key[i % key.Length]) & 0xFF;
                var tmp = s[i];
                s[i] = s[j];
                s[j] = tmp;
            }

            int iidx = 0;
            j = 0;
            // drop 1024 bytes
            for (int n = 0; n < 1024; n++)
            {
                iidx = (iidx + 1) & 0xFF;
                j = (j + s[iidx]) & 0xFF;
                var tmp = s[iidx];
                s[iidx] = s[j];
                s[j] = tmp;
                _ = s[(s[iidx] + s[j]) & 0xFF];
            }

            var outb = new byte[data.Length];
            for (int k = 0; k < data.Length; k++)
            {
                iidx = (iidx + 1) & 0xFF;
                j = (j + s[iidx]) & 0xFF;
                var tmp = s[iidx];
                s[iidx] = s[j];
                s[j] = tmp;
                var rnd = s[(s[iidx] + s[j]) & 0xFF];
                outb[k] = (byte)(data[k] ^ rnd);
            }
            return outb;
        }

        private static string GenSignature64(string method, string path, Dictionary<string, string> values, byte[] signedNonce)
        {
            // Build string as: method + "&" + path + "&data=" + values.Get("data")
            values.TryGetValue("data", out var data);
            var sb = new StringBuilder();
            sb.Append(method).Append("&").Append(path).Append("&data=").Append(data ?? string.Empty);
            if (values.TryGetValue("rc4_hash__", out var rc4) && !string.IsNullOrEmpty(rc4))
            {
                sb.Append("&rc4_hash__=").Append(rc4);
            }
            sb.Append("&").Append(Convert.ToBase64String(signedNonce ?? Array.Empty<byte>()));

            using var sha1 = SHA1.Create();
            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            var hash = sha1.ComputeHash(bytes);
            return Convert.ToBase64String(hash);
        }

        // UnmarshalScaleData parses scale data response JSON and appends Weight items to the weights list.
        // Returns the CreateTime of the 20th item (for pagination), or 0 if fewer than 20 items.
        public long UnmarshalScaleData(byte[] data, List<Weight> weights)
        {
            if (weights == null) throw new ArgumentNullException(nameof(weights));

            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            var items = new List<ScaleDataItem>();

            foreach (var item in root.EnumerateArray())
            {
                items.Add(new ScaleDataItem
                {
                    Model = GetStringProperty(item, "model"),
                    Uid = GetInt64Property(item, "uid"),
                    AccountId = GetInt64Property(item, "accountId"),
                    Did = GetStringProperty(item, "did"),
                    CreateTime = GetInt64Property(item, "createTime"),
                    Data = GetStringProperty(item, "data"),
                    DataVersion = GetIntProperty(item, "dataVersion"),
                    Sn = GetStringProperty(item, "sn"),
                    FromSource = GetIntProperty(item, "fromSource")
                });
            }

            foreach (var v1 in items)
            {
                switch (v1.FromSource)
                {
                    case 1:
                        ParseFromSource1(v1, weights);
                        break;
                    case 2:
                        ParseFromSource2(v1, weights);
                        break;
                    case 3:
                        ParseFromSource3(v1, weights);
                        break;
                }
            }

            if (items.Count < 20)
                return 0;

            return items[19].CreateTime;
        }

        private void ParseFromSource1(ScaleDataItem v1, List<Weight> weights)
        {
            try
            {
                using var doc = JsonDocument.Parse(v1.Data);
                var root = doc.RootElement;

                var w = new Weight
                {
                    Date = DateTimeOffset.FromUnixTimeMilliseconds(v1.CreateTime).UtcDateTime,
                    WeightKg = GetFloat(root, "weight"),
                    BMI = GetFloat(root, "bmi"),
                    BodyFat = GetFloat(root, "bfp"),
                    BodyWater = GetFloat(root, "bwp"),
                    BoneMass = GetFloat(root, "bmc"),
                    MetabolicAge = GetInt(root, "ma"),
                    MuscleMass = GetFloat(root, "slm"),
                    ProteinMass = GetFloat(root, "pm"),
                    VisceralFat = GetInt(root, "vfl"),
                    BasalMetabolism = GetInt(root, "bmr"),
                    BodyScore = GetInt(root, "sbc"),
                    HeartRate = GetInt(root, "heartRate"),
                    SkeletalMuscleMass = GetFloat(root, "smm"),
                };

                if (root.TryGetProperty("reportFrom", out var reportFromEl) && reportFromEl.ValueKind == JsonValueKind.String)
                    w.Source = reportFromEl.GetString();

                if (root.TryGetProperty("user", out var userEl) && userEl.ValueKind == JsonValueKind.Object)
                {
                    if (userEl.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                        w.User = nameEl.GetString();
                    w.WeightKg = ParseAnyFloat(userEl, "height");
                }

                weights.Add(w);
            }
            catch
            {
                // Silently skip malformed entries
            }
        }

        private void ParseFromSource2(ScaleDataItem v1, List<Weight> weights)
        {
            try
            {
                using var doc = JsonDocument.Parse(v1.Data);
                var root = doc.RootElement;

                var w = new Weight
                {
                    Date = DateTimeOffset.FromUnixTimeMilliseconds(v1.CreateTime).UtcDateTime,
                    WeightKg = ParseAnyFloat(root, "weight"),
                    BMI = ParseAnyFloat(root, "bmi"),
                    BodyFat = ParseAnyFloat(root, "bfp"),
                    BodyWater = ParseAnyFloat(root, "bwp"),
                    BoneMass = ParseAnyFloat(root, "bmc"),
                    MetabolicAge = ParseAnyInt(root, "ma"),
                    MuscleMass = ParseAnyFloat(root, "slm"),
                    ProteinMass = ParseAnyFloat(root, "pm"),
                    VisceralFat = ParseAnyInt(root, "vfl"),
                    BasalMetabolism = ParseAnyInt(root, "bmr"),
                    BodyScore = ParseAnyInt(root, "sbc"),
                    HeartRate = ParseAnyInt(root, "heartRate"),
                    SkeletalMuscleMass = ParseAnyFloat(root, "smm"),
                };

                if (root.TryGetProperty("user", out var userEl) && userEl.ValueKind == JsonValueKind.Object)
                {
                    if (userEl.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                        w.User = nameEl.GetString();
                    if (userEl.TryGetProperty("height", out var heightEl))
                        w.WeightKg = ParseAnyFloat(userEl, "height");
                    if (userEl.TryGetProperty("deviceId", out var deviceIdEl) && deviceIdEl.ValueKind == JsonValueKind.String)
                        w.Source = deviceIdEl.GetString();
                }

                weights.Add(w);
            }
            catch
            {
                // Silently skip malformed entries
            }
        }

        private void ParseFromSource3(ScaleDataItem v1, List<Weight> weights)
        {
            try
            {
                using var doc = JsonDocument.Parse(v1.Data);
                var root = doc.RootElement;

                var w = new Weight
                {
                    Date = DateTimeOffset.FromUnixTimeMilliseconds(ParseInt64(GetStringProperty(root, "time"))).UtcDateTime,
                    WeightKg = ParseFloat(GetStringProperty(root, "weight")),
                    BMI = ParseFloat(GetStringProperty(root, "bmi")),
                    HeartRate = GetInt(root, "heartRate"),
                    Source = v1.Did
                };

                if (root.TryGetProperty("user", out var userEl) && userEl.ValueKind == JsonValueKind.Object)
                {
                    if (userEl.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                        w.User = nameEl.GetString();
                }

                var bodyResData = GetStringProperty(root, "bodyResData");
                if (!string.IsNullOrEmpty(bodyResData))
                {
                    try
                    {
                        using var bodyDoc = JsonDocument.Parse(bodyResData);
                        var bodyRoot = bodyDoc.RootElement;

                        w.BodyFat = ParseFloat(GetStringProperty(bodyRoot, "bfp"));
                        w.BodyWater = ParseFloat(GetStringProperty(bodyRoot, "bwp"));
                        w.BoneMass = ParseFloat(GetStringProperty(bodyRoot, "bmc"));
                        w.MetabolicAge = ParseInt(GetStringProperty(bodyRoot, "ma"));
                        w.MuscleMass = ParseFloat(GetStringProperty(bodyRoot, "slm"));
                        w.ProteinMass = ParseFloat(GetStringProperty(bodyRoot, "pm"));
                        w.VisceralFat = ParseInt(GetStringProperty(bodyRoot, "vfl"));
                        w.BasalMetabolism = ParseInt(GetStringProperty(bodyRoot, "bmr"));
                        w.BodyScore = ParseInt(GetStringProperty(bodyRoot, "sbc"));
                        w.SkeletalMuscleMass = ParseFloat(GetStringProperty(bodyRoot, "smm"));
                    }
                    catch
                    {
                        // Silently skip if BodyResData cannot be parsed
                    }
                }

                weights.Add(w);
            }
            catch
            {
                // Silently skip malformed entries
            }
        }

        private static string GetStringProperty(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var p)) return string.Empty;
            if (p.ValueKind == JsonValueKind.String) return p.GetString() ?? string.Empty;
            return string.Empty;
        }

        private static int GetIntProperty(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var p)) return 0;
            if (p.ValueKind == JsonValueKind.Number) return p.GetInt32();
            return 0;
        }

        private static long GetInt64Property(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var p)) return 0;
            if (p.ValueKind == JsonValueKind.Number) return p.GetInt64();
            return 0;
        }

        private static int ParseInt(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            return int.TryParse(s, out var i) ? i : 0;
        }

        private static long ParseInt64(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            return long.TryParse(s, out var i) ? i : 0;
        }

        private static float ParseFloat(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0f;
            return float.TryParse(s, out var f) ? f : 0f;
        }

        private static int ParseAnyInt(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var p)) return 0;
            switch (p.ValueKind)
            {
                case JsonValueKind.String:
                    return ParseInt(p.GetString() ?? string.Empty);
                case JsonValueKind.Number:
                    return p.GetInt32();
                default:
                    return 0;
            }
        }

        private static float ParseAnyFloat(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var p)) return 0f;
            switch (p.ValueKind)
            {
                case JsonValueKind.String:
                    return ParseFloat(p.GetString() ?? string.Empty);
                case JsonValueKind.Number:
                    return p.GetSingle();
                default:
                    return 0f;
            }
        }

        private class ScaleDataItem
        {
            public string Model { get; set; }
            public long Uid { get; set; }
            public long AccountId { get; set; }
            public string Did { get; set; }
            public long CreateTime { get; set; }
            public string Data { get; set; }
            public int DataVersion { get; set; }
            public string Sn { get; set; }
            public int FromSource { get; set; }
        }
    }
}
