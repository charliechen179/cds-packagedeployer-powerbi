using System;
using System.Diagnostics;
using System.Dynamic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Web;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Tooling.Connector;
using Microsoft.Xrm.Tooling.PackageDeployment.CrmPackageExtentionBase;
using Newtonsoft.Json;

namespace DeploymentPackage
{
    // ReSharper disable once InconsistentNaming
    internal static class PowerBIDeployer
    {
        internal static void Deploy(TraceLogger logger, CrmServiceClient service, string pbiConfigPath,
            string pkgFolder)
        {
            // TODO: Improve logging code

            // ReSharper disable once InconsistentNaming
            const string SEPERATOR = "------------------------------------------------------";
            logger.Log(SEPERATOR, TraceEventType.Information);

            logger.Log("Starting to deploy Power BI report(s)... ", TraceEventType.Information);

            if (!File.Exists(pbiConfigPath))
            {
                logger.Log(new FileNotFoundException($"Unable to locate pbiconfig.json at {pbiConfigPath}"));
                logger.Log(SEPERATOR, TraceEventType.Information);
                return;
            }

            var json = File.ReadAllText(pbiConfigPath);

            if (string.IsNullOrWhiteSpace(json))
            {
                logger.Log(new FileLoadException("pbiconfig.json was empty."));
                logger.Log(SEPERATOR, TraceEventType.Information);
                return;
            }

            try
            {
                dynamic data = JsonConvert.DeserializeObject(json);

                if (data == null)
                {
                    logger.Log("Json data was null", TraceEventType.Information);
                    logger.Log(SEPERATOR, TraceEventType.Information);
                    return;
                }

                var clientId = (string) data.clientId;
                var clientSecret = (string) data.clientSecret;
                var username = (string) data.username;
                var password = (string) data.password;

                foreach (var pbiUpdate in data.pbiUpdates)
                {
                    var token = AuthenticationHelper.GetAccessToken(clientId, clientSecret, username, password);

                    // Originally wrote the code using Power BI REST .net client: https://www.nuget.org/packages/Microsoft.PowerBIDeployer.Api/ 
                    // See https://github.com/devkeydet/pbi-vsts-helper for an example.  However, the .net client was causing errors with PackageDeployer.exe.
                    // So I dropped to straight REST calls.
                    var pbiBaseUri = "https://api.powerbi.com/v1.0/myorg";

                    using (var httpClient = new HttpClient())
                    {
                        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

                        // Get groups
                        logger.Log("Getting groups...");
                        var groupEndpoint = $"{pbiBaseUri}/groups";
                        var response = httpClient.GetAsync(groupEndpoint).Result;
                        logger.Log($"Get groups status code: {response.StatusCode}");
                        dynamic responseObject = JsonConvert.DeserializeObject(response.Content.ReadAsStringAsync().Result);
                        var groups = responseObject.value;

                        var groupFound = false;
                        var groupId = "";

                        // Find out if group exists
                        foreach (var group in groups)
                            if (group.name == pbiUpdate.groupName)
                            {
                                groupFound = true;
                                groupId = group.id;
                                logger.Log($"Group id found: {groupId}");
                                break;
                            }

                        // If it doesn't, create it
                        if (!groupFound)
                        {
                            logger.Log("Group not found.  Creating group...");
                            dynamic addGroupBody = new ExpandoObject();
                            addGroupBody.name = pbiUpdate.groupName;
                            var stringContent = new StringContent(JsonConvert.SerializeObject(addGroupBody),
                                Encoding.UTF8, "application/json");
                            response = httpClient.PostAsync(groupEndpoint, stringContent).Result;
                            logger.Log($"Create group status code: {response.StatusCode}");
                            responseObject = JsonConvert.DeserializeObject(response.Content.ReadAsStringAsync().Result);
                            groupId = responseObject.id;
                        }

                        logger.Log($"Starting import process for {pbiUpdate.reportFileName}...");
                        var pbiFilePath = $"{pkgFolder}\\{pbiUpdate.reportFileName}";
                        logger.Log($"Report file path: {pbiFilePath}");
                        var bytes = File.ReadAllBytes(pbiFilePath);

                        var importsEndpoint = $"{groupEndpoint}/{groupId}/imports";
                        var multiPartContent = new MultipartFormDataContent();
                        var byteArrayContent = new ByteArrayContent(bytes);
                        multiPartContent.Add(byteArrayContent);
                        var reportFileName = pbiUpdate.reportFileName.ToString();
                        var importPostUrl =
                            $"{importsEndpoint}?datasetDisplayName={HttpUtility.UrlEncode(reportFileName)}&nameConflict=Overwrite";
                        logger.Log($"Import post url (first attempt): {importPostUrl}");
                        response = httpClient.PostAsync(importPostUrl, multiPartContent).Result;
                        logger.Log($"Import report status code (first attempt): {response.StatusCode}");
                        if (!response.IsSuccessStatusCode)
                        {
                            multiPartContent = new MultipartFormDataContent();
                            byteArrayContent = new ByteArrayContent(bytes);
                            multiPartContent.Add(byteArrayContent);
                            importPostUrl =
                                $"{importsEndpoint}?datasetDisplayName={HttpUtility.UrlEncode(reportFileName)}";
                            logger.Log($"Import post url (second attempt): {importPostUrl}");
                            response = httpClient.PostAsync(importPostUrl, multiPartContent).Result;
                            logger.Log($"Import report status code (second attempt): {response.StatusCode}");
                        }

                        responseObject = JsonConvert.DeserializeObject(response.Content.ReadAsStringAsync().Result);
                        var importId = responseObject.id;

                        string reportId;
                        string datasetId;
                        // Check for Succeeded
                        logger.Log("Checking import status...");
                        while (true)
                        {
                            response = httpClient.GetAsync($"{importsEndpoint}/{importId}").Result;
                            responseObject = JsonConvert.DeserializeObject(response.Content.ReadAsStringAsync().Result);
                            logger.Log($"Import status: {responseObject.importState}...");

                            if (responseObject.importState == "Succeeded")
                            {
                                reportId = responseObject.reports[0].id;
                                datasetId = responseObject.datasets[0].id;
                                break;
                            }

                            Thread.Sleep(TimeSpan.FromSeconds(5));
                        }

                        var datasetsEndpoint = $"{groupEndpoint}/{groupId}/datasets";

                        // Update the data sources so that they point to the right Dynamics instance.
                        logger.Log("Updating datasources...");
                        var content = new StringContent(pbiUpdate.dataSourceUpdates.ToString(), Encoding.UTF8,
                            "application/json");
                        var updatedatasourcesEndpoint = $"{datasetsEndpoint}/{datasetId}/updatedatasources";
                        var result = httpClient.PostAsync(updatedatasourcesEndpoint, content).Result;
                        if (!result.IsSuccessStatusCode)
                            logger.Log(new Exception("ERROR: Failed to update the datasource"));

                        // TODO: would be great if we could update the data source credentials and then refresh it.
                        // Don't see how to do that with the current API:https://msdn.microsoft.com/en-us/library/mt784652.aspx

                        logger.Log("Updating dashboards...");
                        // Update dashboard(s) using the new report id and group id
                        foreach (var dashboardUpdate in pbiUpdate.dashboardUpdates)
                        {
                            var dashboard = service.Retrieve("systemform",
                                new Guid(dashboardUpdate.dashboardId.ToString()), new ColumnSet("formxml"));
                            var formXml = dashboard["formxml"].ToString();
                            formXml = formXml.Replace(dashboardUpdate.reportIdToFind.ToString(), reportId);
                            formXml = formXml.Replace(dashboardUpdate.groupIdToFind.ToString(), groupId);
                            dashboard["formxml"] = formXml;
                            service.Update(dashboard);
                        }
                    }
                }

                logger.Log("Publishing all customizations");
                var publishAllCustomizationsRequest = new PublishAllXmlRequest();
                service.Execute(publishAllCustomizationsRequest);
            }
            catch (Exception ex)
            {
                logger.Log("Unexpected error deploying to Power BI.");
                logger.Log(ex);
            }
            finally
            {
                logger.Log("Finished deploying Power BI report(s)... ", TraceEventType.Information);
                logger.Log(SEPERATOR, TraceEventType.Information);
            }
        }
    }
}