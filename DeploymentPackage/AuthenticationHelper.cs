using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using Newtonsoft.Json;

namespace DeploymentPackage
{
    internal static class AuthenticationHelper
    {
        internal static string GetAccessToken(string clientId, string clientSecret, string username, string password)
        {
            const string resource = "https://analysis.windows.net/powerbi/api";

            using (var authClient = new HttpClient())
            {
                var authContent = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("resource", resource),
                    new KeyValuePair<string, string>("client_id", clientId),
                    new KeyValuePair<string, string>("grant_type", "password"),
                    new KeyValuePair<string, string>("username", username),
                    new KeyValuePair<string, string>("password", password),
                    new KeyValuePair<string, string>("scope", "openid"),
                    new KeyValuePair<string, string>("client_secret", clientSecret),
                });
                var authResult = authClient.PostAsync("https://login.microsoftonline.com/common/oauth2/token", authContent).Result;
                var success = authResult.StatusCode == HttpStatusCode.OK;
                if (success)
                {
                    var responseContent = authResult.Content.ReadAsStringAsync().Result;
                    dynamic responseObject = JsonConvert.DeserializeObject(responseContent);
                    return (string)responseObject.access_token;
                }

                return "";
            }
        }
    }
}