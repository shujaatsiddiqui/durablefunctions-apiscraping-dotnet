using Octokit;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace GithubClientApp
{
    class Program
    {
        static void Main(string[] args)
        {
            Github().Wait();
        }

        public static async Task Github()
        {
            string login = "shujaatsiddiqui";
            //string organizationName = "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";
            string token = "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";
            GitHubClient github = new GitHubClient(new ProductHeaderValue(login)) { Credentials = new Credentials(token) };
            var repositories = (await github.Repository.GetAllForUser(login)).Select(x => (x.Id, x.Name)).ToList();
        }
    }
}
