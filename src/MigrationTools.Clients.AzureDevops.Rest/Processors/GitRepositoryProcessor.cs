using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MigrationTools.DataContracts.Repos;
using MigrationTools.Endpoints;
using MigrationTools.Enrichers;
using MigrationTools.Processors;

namespace MigrationTools.Processors
{
    public class GitRepositoryProcessorOptions : ProcessorOptions
    {
        public Dictionary<string, string> Repositories { get; set; } = new Dictionary<string, string>();

        public override Type ToConfigure => typeof(GitRepositoryProcessor);

        public override IProcessorOptions GetDefault()
        {
            return this;
        }

        public override void SetDefaults()
        {
        }
    }

    internal class RepoMapping
    {
        public GitRepo SourceRepo { get; set; }
        public GitRepo TargetRepo { get; set; }
    }
    public partial class GitRepositoryProcessor : Processor
    {
        private IEnumerable<RepoMapping> Repos { get; set; } = new List<RepoMapping>();
        private GitRepositoryProcessorOptions _Options;

        public new AzureDevOpsEndpoint Source => (AzureDevOpsEndpoint)base.Source;

        public new AzureDevOpsEndpoint Target => (AzureDevOpsEndpoint)base.Target;

        public GitRepositoryProcessor(
                    ProcessorEnricherContainer processorEnrichers,
                    IEndpointFactory endpointFactory,
                    IServiceProvider services,
                    ITelemetryLogger telemetry,
                    ILogger<Processor> logger)
            : base(processorEnrichers, endpointFactory, services, telemetry, logger)
        {

        }


        public override void Configure(IProcessorOptions options)
        {
            base.Configure(options);
            Log.LogInformation("GitRepositoryProcessor::Configure");
            _Options = (GitRepositoryProcessorOptions)options;
        }

        protected override void InternalExecute()
        {
            Log.LogInformation("Processor::InternalExecute::Start");
            EnsureConfigured();
            Migrate().GetAwaiter().GetResult();
            Log.LogInformation("Processor::InternalExecute::End");
        }

        private void EnsureConfigured()
        {
            Log.LogInformation("Processor::EnsureConfigured");
            if (_Options == null)
            {
                throw new Exception("You must call Configure() first");
            }
            if (Source is not AzureDevOpsEndpoint)
            {
                throw new Exception("The Source endpoint configured must be of type AzureDevOpsEndpoint");
            }
            if (Target is not AzureDevOpsEndpoint)
            {
                throw new Exception("The Target endpoint configured must be of type AzureDevOpsEndpoint");
            }

            if (_Options.Repositories == null || _Options.Repositories.Count == 0)
            {
                _Options.Repositories.Add("*", "*");
            }

            if (Source.Options.Name == Target.Options.Name)
            {
                throw new Exception("Source and Target need to be defined independently for parallel processing to work.");
            }
        }

        private async Task Migrate()
        {
            /*
             * Overall outline:
             * ----------------
             * 1 - Resolve all source repos that match the configuration
             * 2 - Resolve all target repos that match the configuration and source
             * 3 - Iterate through repos:
             *     A - Clone Repo (include repo history or not as configured)
             *     B - Fetch all remote branches and tags
             *     C - Add git remote for target
             *     D - Push to target remote
             * 4 - Using Rest API, migrate all pull requests
             * 5 - ??
             */
            /*
            Repos = from repo in (await Source.GetApiDefinitionsAsync<GitRepo>()).ToList()
                    where
                        _Options.Repositories.ContainsKey("*") ||
                        _Options.Repositories.Keys.Any(r => r.Equals(repo.Name, StringComparison.OrdinalIgnoreCase))
                    select new RepoMapping() { SourceRepo = repo, NameMatch };

            Log.LogInformation($"Found [{Repos.Count()}] repositories in source that need to be migrated.");

            var targetRepos = await Target.GetApiDefinitionsAsync<GitRepo>();

            foreach (var map in Repos)
            {
                map.TargetRepo = targetRepos.FirstOrDefault(x => x.Name.Equals(map.SourceRepo.Name))
            }
            */
        }
    }
}
