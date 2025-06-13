using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System;
using System.Threading.Tasks;
using System.IO;
using Microsoft.Graph;
using Azure.Identity;
using Microsoft.Graph.Me.SendMail;
using Microsoft.Graph.Models;
using SendGrid;
using SendGrid.Helpers.Mail;
using DotNetEnv;
using Azure.Data.Tables;
using ghcpfunc.Models;

namespace ghcpfunc
{
    
    public class ghcpfunc
    {
        
        private readonly ILogger<ghcpfunc> _logger;
        private TableClient _warnTableClient;
        private TableClient _inactiveTableClient;

        public ghcpfunc(ILogger<ghcpfunc> logger)
        {
            _logger = logger;

        }
        private async Task ClearAndWriteTableAsync(TableClient tableClient, string partitionKey, List<(string Username, DateTime LastActivity, string externalId)> users)
        {
            await foreach (var entity in tableClient.QueryAsync<UserEntity>(e => e.PartitionKey == partitionKey))
            {
                await tableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey);
            }

            foreach (var user in users)
            {
                await tableClient.AddEntityAsync(new UserEntity
                {
                    PartitionKey = partitionKey,
                    RowKey = user.externalId,
                    LastActivity = user.LastActivity,
                    Username = user.Username
                });
            }
        }

        [Function("ghcpfunc")]
        public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequest req)
        //public async Task Run([TimerTrigger("0 0 9 * * *")] TimerInfo timerInfo)
        {
            _logger.LogInformation("C# HTTP trigger function processed a request.");
            Env.Load();
        
            try
            {
                // Read environment variables 
                string key = Environment.GetEnvironmentVariable("GITHUB_API_KEY") ?? string.Empty;
                string enterprise = Environment.GetEnvironmentVariable("enterprise") ?? string.Empty;
                string clientId = Environment.GetEnvironmentVariable("clientId") ?? string.Empty;
                string tenantId = Environment.GetEnvironmentVariable("tenantId") ?? string.Empty;
                string clientSecret = Environment.GetEnvironmentVariable("clientSecret") ?? string.Empty;
                string groupId = Environment.GetEnvironmentVariable("groupId") ?? string.Empty;
                string sendGridAPIKey = Environment.GetEnvironmentVariable("sendGridAPIKey") ?? string.Empty;
                string emailSender = Environment.GetEnvironmentVariable("emailSender") ?? string.Empty;
                string storageConn = Environment.GetEnvironmentVariable("AzureWebJobsStorage") ?? string.Empty;

                _warnTableClient = new TableClient(storageConn, "WarnUsers");
                _inactiveTableClient = new TableClient(storageConn, "InactiveUsers");
                _warnTableClient.CreateIfNotExists();
                _inactiveTableClient.CreateIfNotExists();

                double daysRemove = 45; 
                double daysWarning = 30;


                if (string.IsNullOrEmpty(key))
                {
                    _logger.LogError("GitHub API Key is missing.");
                    return new BadRequestObjectResult("GitHub API Key is required");
                }

                if (string.IsNullOrEmpty(enterprise))
                {
                    _logger.LogError("GitHub enterprise name is missing.");
                    return new BadRequestObjectResult("GitHub enterpise name is required");
                }

                var (inactiveUsers, warnUsers) = await GitHubHelper.GetInactiveUsers(key, enterprise, daysRemove, daysWarning, _logger);

                List<(string Username, DateTime LastActivity, string externalId)> inactiveUserList = inactiveUsers;
                List<(string Username, DateTime LastActivity, string externalId)> warnUserList = warnUsers;

                // Convert inactiveUsers to a serializable format
                var serializableInactiveUsers = inactiveUsers.Select(user => new
                {
                    Username = user.Username,
                    LastActivity = user.LastActivity,
                    ExternalId = user.externalId
                }).ToList();

                // Convert warnUsers to a serializable format
                var serializableWarnUsers = warnUsers.Select(user => new
                {
                    Username = user.Username,
                    LastActivity = user.LastActivity,
                    ExternalId = user.externalId
                }).ToList();

                // Log the serialized lists
                _logger.LogInformation("Inactive Users: {InactiveUsers}", JsonSerializer.Serialize(serializableInactiveUsers));
                _logger.LogInformation("Warned Users: {WarnUsers}", JsonSerializer.Serialize(serializableWarnUsers));
                _logger.LogInformation($"Found {inactiveUsers.Count} inactive users");
                _logger.LogInformation($"Found {warnUsers.Count} warned users");


                //ENTRA STUFF


                var scopes = new[] { "https://graph.microsoft.com/.default" };

                // Values from app registration

                var options = new ClientSecretCredentialOptions
                {
                    AuthorityHost = AzureAuthorityHosts.AzurePublicCloud,
                };

                var clientSecretCredential = new ClientSecretCredential(
                    tenantId, clientId, clientSecret, options);

                var graphClient = new GraphServiceClient(clientSecretCredential, scopes);

                //REMOVE USERS FROM ENTRA GROUP

                foreach (var user in inactiveUserList)
                {
                    //FIND USER IN ENTRA

                    try
                    {
                        var entraUser = await graphClient.Users.GetAsync((requestConfiguration) =>
                        {
                            requestConfiguration.QueryParameters.Filter =
                                $"startswith(userPrincipalName,'{user.Username}') or startswith(mailNickname,'{user.Username}') or startswith(employeeId,'{user.externalId}')";
                            requestConfiguration.QueryParameters.Select = new string[]
                            {
                                "id", "displayName", "userPrincipalName", "mail", "mailNickname", "identities"
                            };
                            requestConfiguration.QueryParameters.Top = 1;


                        });
                        var userId = entraUser.Value.FirstOrDefault()?.Id;
                        if (userId != null)
                        {
                            await graphClient.Groups[groupId].Members[userId].Ref.DeleteAsync();
                            _logger.LogInformation($"Removed user {user.Username} from group {groupId}");
                        }
                        if (entraUser == null || entraUser.Value == null || !entraUser.Value.Any())
                        {
                            _logger.LogWarning($"No matching Entra user found for username: {user.Username}, externalId: {user.externalId}");
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Error occurred while fetching Entra user for username: {user.Username}, externalId: {user.externalId}. Exception: {ex.Message}");
                        continue;
                    }



                }


                // SEND WARNING EMAILS
                foreach (var user in warnUserList)
                {
                    var exists = await _warnTableClient.GetEntityIfExistsAsync<UserEntity>("WarnUser", user.externalId); //CHECKING ROWKEY == EXTERNAL ID
                    if (!exists.HasValue)
                    {
                        try
                        {
                            var entraUser = await graphClient.Users.GetAsync((requestConfiguration) =>
                            {
                                requestConfiguration.QueryParameters.Filter =
                                    $"startswith(userPrincipalName,'{user.Username}') or startswith(mailNickname,'{user.Username}') or startswith(employeeId,'{user.externalId}')"; 
                                requestConfiguration.QueryParameters.Select = new string[]
                                {
                                    "id", "displayName", "userPrincipalName", "mail", "mailNickname", "identities"
                                };
                                requestConfiguration.QueryParameters.Top = 1;
                            });


                            if (entraUser == null || entraUser.Value == null || !entraUser.Value.Any())
                            {
                                _logger.LogWarning($"No matching Entra user found for username: {user.Username}, externalId: {user.externalId}");
                                return new NotFoundObjectResult($"No matching Entra user found for username: {user.Username}, externalId: {user.externalId}");
                            }
                            _logger.LogInformation($"Found Entra user: {entraUser.Value.FirstOrDefault()?.DisplayName} with email: {entraUser.Value.FirstOrDefault()?.Mail}");

                            // USING SENDGRID TO SEND EMAIL

                            var client = new SendGridClient(sendGridAPIKey);
                            var from = new SendGrid.Helpers.Mail.EmailAddress(emailSender, "");
                            var subject = "GitHub Copilot Inactivity Warning - Action Required";
                            var to = new SendGrid.Helpers.Mail.EmailAddress(entraUser.Value.FirstOrDefault()?.Mail, entraUser.Value.FirstOrDefault()?.DisplayName);
                            var plainTextContent = $"Dear {entraUser.Value.FirstOrDefault()?.DisplayName},\n\n" +
                                                "We have noticed that you have not been active on GitHub Copilot for a while. " +
                                                "Please log in to your account and use the service to avoid being removed from the group.\n\n" +
                                                "Best regards,\n" +
                                            "Your Team";
                            var htmlContent = "";
                            var msg = MailHelper.CreateSingleEmail(from, to, subject, plainTextContent, htmlContent);
                            var response = await client.SendEmailAsync(msg).ConfigureAwait(false);


                            // USING GRAPH API TO SEND EMAIL (REQUIRES EXCHANGE LISENCE)    
                            //     var requestBodyMail = new SendMailPostRequestBody
                            //     {
                            //         Message = new Message
                            //         {
                            //             Subject = "GitHub Copilot Inactivity Warning - Action Required",
                            //             Body = new ItemBody
                            //             {
                            //                 ContentType = BodyType.Text,
                            //                 Content = $"Dear {entraUser.Value.FirstOrDefault()?.DisplayName},\n\n" +
                            //                         "We have noticed that you have not been active on GitHub Copilot for a while. " +
                            //                         "Please log in to your account and use the service to avoid being removed from the group.\n\n" +
                            //                         "Best regards,\n" +
                            //                         "Your Team",
                            //             },
                            //             ToRecipients = new List<Recipient>
                            //             {
                            //                 new Recipient
                            //                 {
                            //                     EmailAddress = new EmailAddress
                            //                     {
                            //                         Address = entraUser.Value.FirstOrDefault()?.Mail,
                            //                     },
                            //                 },
                            //             },
                            //             // From = new Recipient
                            //             // {
                            //             //     EmailAddress = new EmailAddress
                            //             //     {
                            //             //         Address = "email_insert", 
                            //             //     },
                            //             // },
                            //         },
                            //         SaveToSentItems = false,
                            //     };

                            //     await graphClient.Users["email_insert"].SendMail.PostAsync(new Microsoft.Graph.Users.Item.SendMail.SendMailPostRequestBody
                            //     {
                            //         Message = requestBodyMail.Message,
                            //         SaveToSentItems = requestBodyMail.SaveToSentItems
                            //     });
                            //     _logger.LogInformation($"Warning email sent to user: {user.Username}");
                            //     await graphClient.Me.SendMail.PostAsync(requestBodyMail);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"Error occurred while processing warning email for username: {user.Username}, externalId: {user.externalId}. Exception: {ex.Message}");
                            continue;
                        }

                    }

                    else
                    {
                        _logger.LogInformation($"Warning email already sent to {user.Username}, skipping.");
                    }

                    await ClearAndWriteTableAsync(_warnTableClient, "WarnUser", warnUsers);
                    await ClearAndWriteTableAsync(_inactiveTableClient, "InactiveUser", inactiveUsers);
                }




                var result = new
                {
                    InactiveUsers = serializableInactiveUsers,
                    WarnedUsers = serializableWarnUsers
                };
                return new OkObjectResult(JsonSerializer.Serialize(result));
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error occurred: {ex.Message}");
                return new ObjectResult($"Error: {ex.Message}") { StatusCode = 500 };
            }
        }
    }
}
