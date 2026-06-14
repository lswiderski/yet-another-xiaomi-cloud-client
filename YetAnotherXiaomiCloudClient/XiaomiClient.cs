using Flurl.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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
        private string _sid { get; }
        private string _cookies { get; set; }
        private long _userId { get; set; }
        private byte[] _ssecurity { get; set; }
        private string _passToken { get; set; }
        private string _agent;
        public bool IsAuthenticated { get; private set; } = false;

        public XiaomiClient(string app)
        {
            _sid = app;
            _agent = GenerateAgent();
        }


        // Generates a random User-Agent similar to the original Python implementation.
        public static string GenerateAgent()
        {
            // agent_id: 13 characters from ASCII 65..69 (A..E)
            var agentIdChars = new char[13];
            for (int i = 0; i < agentIdChars.Length; i++)
            {
                int v = RandomNumberGenerator.GetInt32(65, 70); // upper bound exclusive
                agentIdChars[i] = (char)v;
            }

            // random_text: 18 lowercase characters from ASCII 97..122 (a..z)
            var randomTextChars = new char[18];
            for (int i = 0; i < randomTextChars.Length; i++)
            {
                int v = RandomNumberGenerator.GetInt32(97, 123);
                randomTextChars[i] = (char)v;
            }

            var agentId = new string(agentIdChars);
            var randomText = new string(randomTextChars);
            return $"{randomText}-{agentId} APP/com.xiaomi.mihome APPV/10.5.201";
        }

        public async Task<bool> IsTokenValid(long userId, string passToken)
        {
            try
            {
                await LoginWithToken(userId, passToken);
                return true;
            }
            catch (Exception ex)
            {
                // ignore any error, just return false
                return false;
            }
        }

        public async Task LoginWithToken(long userId, string passToken)
        {
            if (string.IsNullOrEmpty(passToken)) throw new ArgumentException("passToken is empty", nameof(passToken));


            var loginResultRaw = await "https://account.xiaomi.com/pass/serviceLogin?_json=true&sid=xiaomiio"
                .WithHeader("User-Agent", _agent)
                .WithHeader("Content-Type", "application/x-www-form-urlencoded")
                .WithHeader("Cookie", $"userId={userId}; passToken={passToken}")
                .GetAsync()
                .ReceiveString();

            var skippedStartString = loginResultRaw.Substring(11);

            var loginResult = JsonSerializer.Deserialize<LoginResult>(skippedStartString);
            if (loginResult == null)
            {
                throw new Exception("failed to parse login result");
            }
            if (loginResult.Result != "ok")
            {
                throw new Exception("login failed: " + loginResult.Result);
            }

            _userId = loginResult.UserId;
            _passToken = loginResult.PassToken;
            _ssecurity = loginResult?.Ssecurity;
            IsAuthenticated = true;
            // Cookies = $"userId={userId}; passToken={passToken}";
            var location = loginResult?.Location ?? throw new InvalidOperationException("login result Location is null");
            await ServiceLogin3Async(location);
        }

        // Completes login by following the provided location URL and collecting Set-Cookie headers.
        // This mirrors the Go serviceLogin3 behaviour: perform GET to location and append all Set-Cookie
        // values (only the name=value part before the ";") into the Cookies property so RequestAsync
        // reuses them.
        public async Task ServiceLogin3Async(string location)
        {
            if (string.IsNullOrEmpty(location)) throw new ArgumentException("location is empty", nameof(location));

            var res = await location.GetAsync();

            var cookies = res.Headers.GetAll("Set-Cookie") ?? Array.Empty<string>();
            if (cookies.Any())
            {
                foreach (var s in cookies)
                {
                    var cookiePair = s.Split(';', 2)[0].Trim();
                    if (string.IsNullOrEmpty(cookiePair)) continue;
                    if (!string.IsNullOrEmpty(_cookies)) _cookies += "; ";
                    _cookies += cookiePair;
                }
            }
        }

        public async Task<string> GetModelWeightsRawJson(string region, string model)
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            byte[] data = null;
            switch (region)
            {
                case "":
                case "cn":
                    data = await GetScalseDataChina(model, ts);
                    break;

                case "de":
                case "i2":
                case "ru":
                case "sg":
                case "us":
                   data = await GetScaleDataGlobal(region, model, ts);
                    break;

                default:
                    throw new ArgumentException($"xiaomi: unsupported region: {region}");
            }
            string json = Encoding.UTF8.GetString(data);
            return json;
        }

        public async Task<List<Weight>> GetModelWeights(string region, string model, int? maxEntries = null)
        {
            var weights = new List<Weight>();

            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            switch (region)
            {
                case "":
                case "cn":
                    while (ts > 0 && (maxEntries == null || weights.Count < maxEntries))
                    {
                        byte[] data = await GetScalseDataChina(model, ts);
                        ts = UnmarshalScaleData(data, weights);
                    }
                    break;

                case "de":
                case "i2":
                case "ru":
                case "sg":
                case "us":
                    while (ts > 0 && (maxEntries == null || weights.Count < maxEntries))
                    {
                        byte[] data = await GetScaleDataGlobal(region, model, ts);
                        ts = UnmarshalScaleData(data, weights);
                    }
                    break;

                default:
                    throw new ArgumentException($"xiaomi: unsupported region: {region}");
            }

            return weights;
        }

        private async Task<byte[]> GetScalseDataChina(string model, long ts)
        {
            var parameters = $"{{\"param\":{{\"endTime\":1,\"beginTime\":{ts}}},\"model\":\"{model}\",\"uid\":{_userId},\"did\":0}}";
            var data = await RequestAsync(
                "https://api.io.mi.com/app", "/eco/scale/getData", parameters,
                new Dictionary<string, string> { { "MIOT-REQUEST-MODEL", model } }
            );
            return data;
        }

        private async Task<byte[]> GetScaleDataGlobal(string region, string model, long ts)
        {
            var parameters = $"{{\"endTime\":1,\"beginTime\":{ts},\"model\":\"{model}\",\"uid\":\"{_userId}\",\"did\":0,\"accountId\":0}}";
            var data = await RequestAsync(
                $"https://{region}.api.io.mi.com/app", "/eco/common/scale/getUserDataByPage", parameters,
                new Dictionary<string, string> { { "MIOT-REQUEST-MODEL", model } }
            );
            return data;
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

        public async Task<byte[]> RequestAsync(string baseUrl, string apiUrl, string parameters, Dictionary<string, string> headers)
        {
            var values = new Dictionary<string, string>
            {
                ["data"] = parameters
            };

            var nonce = GenNonce();

            var signedNonce = GenSignedNonce(_ssecurity, nonce);

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
               .WithHeader("Cookie", _cookies)
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
