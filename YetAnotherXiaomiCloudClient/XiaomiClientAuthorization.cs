using Flurl.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YetAnotherXiaomiCloudClient
{
    /// <summary>
    /// QR Code based authorization for Xiaomi Cloud login.
    /// Implements the 4-step QR code login flow.
    /// </summary>
    public class XiaomiClientAuthorization
    {
        private readonly string _agent;

        // Tokens extracted during login
        public long UserId { get; private set; }
        public string? PassToken { get; private set; }
        public string? CUserId { get; private set; }
        public byte[]? Ssecurity { get; private set; }
        public string? ServiceToken { get; private set; }
        public string? Location { get; private set; }

        // Internal state for login flow
        private string? _qrImageUrl;
        private string? _loginUrl;
        private string? _longPollingUrl;
        private int _timeout;

        public string? LoginUrl => _loginUrl;

        public XiaomiClientAuthorization()
        {
            _agent = GenerateAgent();
        }

        public XiaomiClientAuthorization(string agent)
        {
            _agent = agent;
        }

        /// <summary>
        /// Main login flow using QR code. Executes all 4 steps.
        /// </summary>
        public async Task<bool> LoginAsync()
        {
            try
            {
                if (!await LoginStep1Async())
                {
                    System.Diagnostics.Debug.WriteLine("Unable to get login message.");
                    return false;
                }

                if (await LoginStep2Async() == null)
                {
                    System.Diagnostics.Debug.WriteLine("Unable to get login QR Image.");
                    return false;
                }

                try
                {
                    if (!await LoginStep3Async())
                    {
                        System.Diagnostics.Debug.WriteLine("Unable to login via QR scan.");
                        return false;
                    }
                }
                catch (InvalidOperationException ex)
                {
                    System.Diagnostics.Debug.WriteLine($"QR Code login failed: {ex.Message}");
                    throw;
                }

                if (!await LoginStep4Async())
                {
                    System.Diagnostics.Debug.WriteLine("Unable to get service token.");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Login error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Step 1: Get login message with QR code URL and long polling URL
        /// </summary>
        public async Task<bool> LoginStep1Async()
        {
            System.Diagnostics.Debug.WriteLine("login_step_1");
            const string url = "https://account.xiaomi.com/longPolling/loginUrl";

            var parameters = new Dictionary<string, string>
            {
                { "_qrsize", "480" },
                { "qs", "%3Fsid%3Dxiaomiio%26_json%3Dtrue" },
                { "callback", "https://sts.api.io.mi.com/sts" },
                { "_hasLogo", "false" },
                { "sid", "xiaomiio" },
                { "serviceParam", "" },
                { "_locale", "en_GB" },
                { "_dc", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString() }
            };

            try
            {
                var responseText = await url
                    .WithHeader("User-Agent", _agent)
                    .GetAsync()
                    .ReceiveString();

                System.Diagnostics.Debug.WriteLine(responseText);

                var responseData = ParseJsonResponse(responseText);
                if (responseData.HasValue)
                {
                    var root = responseData.Value;
                    if (root.TryGetProperty("qr", out var qrElement) &&
                        root.TryGetProperty("loginUrl", out var loginUrlElement) &&
                        root.TryGetProperty("lp", out var lpElement) &&
                        root.TryGetProperty("timeout", out var timeoutElement))
                    {
                        _qrImageUrl = qrElement.GetString();
                        _loginUrl = loginUrlElement.GetString();
                        _longPollingUrl = lpElement.GetString();
                        _timeout = timeoutElement.GetInt32();
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Step 1 exception: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Step 2: Fetch QR code image and return it for display
        /// </summary>
        public async Task<byte[]?> LoginStep2Async()
        {
            System.Diagnostics.Debug.WriteLine("login_step_2");

            if (string.IsNullOrEmpty(_qrImageUrl))
            {
                System.Diagnostics.Debug.WriteLine("QR image URL is not available");
                return null;
            }

            try
            {
                var imageBytes = await _qrImageUrl
                    .WithHeader("User-Agent", _agent)
                    .GetBytesAsync();

                System.Diagnostics.Debug.WriteLine($"QR code fetched, size: {imageBytes.Length} bytes");
                System.Diagnostics.Debug.WriteLine($"Login URL: {_loginUrl}");

                return imageBytes;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Step 2 exception: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Step 3: Long polling for QR scan result and extract tokens
        /// Maximum 10 timeout attempts allowed before throwing exception.
        /// </summary>
        public async Task<bool> LoginStep3Async()
        {
            System.Diagnostics.Debug.WriteLine("login_step_3");

            if (string.IsNullOrEmpty(_longPollingUrl))
            {
                System.Diagnostics.Debug.WriteLine("Long polling URL is not available");
                return false;
            }

            var startTime = DateTime.UtcNow;
            var timeout = TimeSpan.FromSeconds(_timeout);
            int timeoutAttempts = 0;
            const int maxTimeoutAttempts = 10;

            try
            {
                while (DateTime.UtcNow - startTime < timeout)
                {
                    try
                    {
                        var responseText = await _longPollingUrl
                            .WithHeader("User-Agent", _agent)
                            .WithTimeout(TimeSpan.FromSeconds(10))
                            .GetAsync()
                            .ReceiveString();

                        System.Diagnostics.Debug.WriteLine("Long polling successful!");
                        System.Diagnostics.Debug.WriteLine($"Response: {responseText}");

                        var responseData = ParseJsonResponse(responseText);
                        if (responseData.HasValue)
                        {
                            var root = responseData.Value;
                            // Extract tokens from response
                            if (root.TryGetProperty("userId", out var userIdElement))
                                UserId = userIdElement.GetInt64();

                            if (root.TryGetProperty("ssecurity", out var ssecurityElement))
                            {
                                var ssecurityStr = ssecurityElement.GetString();
                                if (!string.IsNullOrEmpty(ssecurityStr))
                                    Ssecurity = Convert.FromBase64String(ssecurityStr);
                            }

                            if (root.TryGetProperty("cUserId", out var cUserIdElement))
                                CUserId = cUserIdElement.GetString();

                            if (root.TryGetProperty("passToken", out var passTokenElement))
                                PassToken = passTokenElement.GetString();

                            if (root.TryGetProperty("location", out var locationElement))
                                Location = locationElement.GetString();

                            System.Diagnostics.Debug.WriteLine($"User ID: {UserId}");
                            System.Diagnostics.Debug.WriteLine($"CUser ID: {CUserId}");
                            System.Diagnostics.Debug.WriteLine($"Pass Token: {PassToken}");
                            System.Diagnostics.Debug.WriteLine($"Location: {Location}");

                            return true;
                        }
                    }
                    catch (Flurl.Http.FlurlHttpTimeoutException ex)
                    {
                        timeoutAttempts++;
                        System.Diagnostics.Debug.WriteLine($"Long polling timed out (attempt {timeoutAttempts}/{maxTimeoutAttempts}), retrying...");

                        if (timeoutAttempts >= maxTimeoutAttempts)
                        {
                            throw new InvalidOperationException(
                                $"Long polling failed: Maximum timeout attempts ({maxTimeoutAttempts}) exceeded. " +
                                $"Unable to receive QR code scan confirmation.", ex);
                        }
                        continue;
                    }

                    await Task.Delay(100);
                }

                System.Diagnostics.Debug.WriteLine($"Long polling timed out after {_timeout} seconds");
            }
            catch (InvalidOperationException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Step 3 critical error: {ex.Message}");
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Step 3 exception: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Step 4: Fetch service token from location URL
        /// </summary>
        public async Task<bool> LoginStep4Async()
        {
            System.Diagnostics.Debug.WriteLine("login_step_4");
            System.Diagnostics.Debug.WriteLine("Fetching service token...");

            if (string.IsNullOrEmpty(Location))
            {
                System.Diagnostics.Debug.WriteLine("No location found");
                return false;
            }

            try
            {
                var response = await Location
                    .WithHeader("User-Agent", _agent)
                    .WithHeader("Content-Type", "application/x-www-form-urlencoded")
                    .GetAsync();

                // Extract serviceToken from Set-Cookie headers
                var setCookieHeaders = response.Headers.GetAll("Set-Cookie") ?? Array.Empty<string>();
                foreach (var cookie in setCookieHeaders)
                {
                    if (cookie.Contains("serviceToken="))
                    {
                        // Extract the token value
                        var parts = cookie.Split(';')[0];
                        var keyValue = parts.Split('=');
                        if (keyValue.Length == 2)
                        {
                            ServiceToken = keyValue[1].Trim();
                            System.Diagnostics.Debug.WriteLine($"Service token: {ServiceToken}");
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Step 4 exception: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Parses JSON response, removing the "&&&START&&&" prefix if present
        /// </summary>
        private static JsonElement? ParseJsonResponse(string responseText)
        {
            try
            {
                // Remove JSON prefix if present
                var cleanedResponse = responseText.Replace("&&&START&&&", "");
                var jsonDocument = JsonDocument.Parse(cleanedResponse);
                return jsonDocument.RootElement;
            }
            catch (JsonException ex)
            {
                System.Diagnostics.Debug.WriteLine($"JSON parse error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Generates a random User-Agent similar to the original Python implementation.
        /// </summary>
        private static string GenerateAgent()
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
    }
}
