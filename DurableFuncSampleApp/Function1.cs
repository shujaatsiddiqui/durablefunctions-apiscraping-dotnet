using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Net.Http;
using Octokit;
using Microsoft.WindowsAzure.Storage;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.WindowsAzure.Storage.Table;
using System.Collections.Generic;
using System.Linq;

namespace DurableFuncSampleApp
{
    public static class Function1
    {
        private static string loginName = "shujaatsiddiqui";
        private static Func<string> getToken = () => "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";//Environment.GetEnvironmentVariable("GitHubToken", EnvironmentVariableTarget.Process);
        private static GitHubClient github = new GitHubClient(new ProductHeaderValue(loginName)) { Credentials = new Credentials(getToken()) };
        private static CloudStorageAccount account = CloudStorageAccount.Parse(Environment.GetEnvironmentVariable("AzureWebJobsStorage", EnvironmentVariableTarget.Process));

        [FunctionName("Function1")]
        public static async Task<HttpResponseMessage> HttpStart(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestMessage req,
            [DurableClient] IDurableOrchestrationClient client,
            ILogger log)
        {
            string instanceId = await client.StartNewAsync("Orchestrator", null, "Nuget");

            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");

            return client.CreateCheckStatusResponse(req, instanceId);
        }

        [FunctionName("Orchestrator")]
        public static async Task<string> RunOrchestrator(
            [OrchestrationTrigger] IDurableOrchestrationContext context,
            ILogger log)
        {
            log.LogInformation($"Started orchestration");
            // retrieves the organization name from the Orchestrator_HttpStart function
            var organizationName = context.GetInput<string>();
            // retrieves the list of repositories for an organization by invoking a separate Activity Function.
            var repositories = await context.CallActivityAsync<List<(long id, string name)>>("GetAllRepositoriesForOrganization", organizationName);

            // Creates an array of task to store the result of each functions
            var tasks = new Task<(long id, int openedIssues, string name)>[repositories.Count];

            log.LogInformation($"Repo Count = '{repositories.Count}'.");

            for (int i = 0; i < repositories.Count; i++)
            {
                // Starting a `GetOpenedIssues` activity WITHOUT `async`
                // This will starts Activity Functions in parallel instead of sequentially.
                tasks[i] = context.CallActivityAsync<(long, int, string)>("GetOpenedIssues", (repositories[i]));
            }

            // Wait for all Activity Functions to complete execution
            await Task.WhenAll(tasks);

            // Retrieve the result of each Activity Function and return them in a list
            var openedIssues = tasks.Select(x => x.Result).ToList();
            log.LogInformation($"open issues Count = '{openedIssues.Count}'.");
            // Send the list to an Activity Function to save them to Blob Storage.
            await context.CallActivityAsync("SaveRepositories", openedIssues);

            return context.InstanceId;
        }

        [FunctionName("GetAllRepositoriesForOrganization")]
        public static async Task<List<(long id, string name)>> GetAllRepositoriesForOrganization([ActivityTrigger] IDurableActivityContext context,
            ILogger log)
        {
            log.LogInformation("started GetAllRepositoriesForOrganization");
            // retrieves the organization name from the Orchestrator function
            var organizationName = context.GetInput<string>();
            // invoke the API to retrieve the list of repositories of a specific organization
            var repositories = (await github.Repository.GetAllForUser(loginName)).Select(x => (x.Id, x.Name)).ToList();
            return repositories;
        }

        [FunctionName("GetOpenedIssues")]
        public static async Task<(long id, int openedIssues, string name)> GetOpenedIssues([ActivityTrigger] IDurableActivityContext context)
        {
            // retrieve a tuple of repositoryId and repository name from the Orchestrator function
            var parameters = context.GetInput<(long id, string name)>();

            // retrieves a list of issues from a specific repository
            var issues = (await github.Issue.GetAllForRepository(parameters.id)).ToList();

            // returns a tuple of the count of opened issues for a specific repository
            return (parameters.id, issues.Count(x => x.State == ItemState.Open), parameters.name);
        }

        [FunctionName("SaveRepositories")]
        public static async Task SaveRepositories([ActivityTrigger] IDurableActivityContext context,
            ILogger log)
        {
            // retrieves a tuple from the Orchestrator function
            var parameters = context.GetInput<List<(long id, int openedIssues, string name)>>();

            // create the client and table reference for Blob Storage
            var client = account.CreateCloudTableClient();
            var table = client.GetTableReference("Repositories");

            // create the table if it doesn't exist already.
            await table.CreateIfNotExistsAsync();

            // creates a batch of operation to be executed
            var batchOperation = new TableBatchOperation();
            batchOperation.Add(TableOperation.InsertOrMerge(new Repository(123123)
            {
                OpenedIssues = 2,
                RepositoryName = "test"
            }));

            foreach (var parameter in parameters)
            {
                // Creates an operation to add the repository to Table Storage
                batchOperation.Add(TableOperation.InsertOrMerge(new Repository(parameter.id)
                {
                    OpenedIssues = parameter.openedIssues,
                    RepositoryName = parameter.name
                }));
            }

            await table.ExecuteBatchAsync(batchOperation);


            log.LogInformation("sample data saved");
        }

        public class Repository : TableEntity
        {
            public Repository(long id)
            {
                PartitionKey = "Default";
                RowKey = id.ToString();
            }
            public int OpenedIssues { get; set; }
            public string RepositoryName { get; set; }
        }
    }
}
