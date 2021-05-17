﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MigrationTools.DataContracts;
using MigrationTools.DataContracts.Pipelines;
using MigrationTools.DataContracts.Repos;
using MigrationTools.Endpoints;
using MigrationTools.Enrichers;

namespace MigrationTools.Processors
{
    /// <summary>
    /// Azure DevOps Processor that migrates Taskgroups, Build- and Release Pipelines.
    /// </summary>
    public partial class AzureDevOpsPipelineProcessor : Processor
    {
        private AzureDevOpsPipelineProcessorOptions _Options;

        public AzureDevOpsPipelineProcessor(
                    ProcessorEnricherContainer processorEnrichers,
                    IEndpointFactory endpointFactory,
                    IServiceProvider services,
                    ITelemetryLogger telemetry,
                    ILogger<Processor> logger)
            : base(processorEnrichers, endpointFactory, services, telemetry, logger)
        {

        }

        public new AzureDevOpsEndpoint Source => (AzureDevOpsEndpoint)base.Source;

        public new AzureDevOpsEndpoint Target => (AzureDevOpsEndpoint)base.Target;

        public override void Configure(IProcessorOptions options)
        {
            base.Configure(options);
            Log.LogInformation("AzureDevOpsPipelineProcessor::Configure");
            _Options = (AzureDevOpsPipelineProcessorOptions)options;
        }

        protected override void InternalExecute()
        {
            Log.LogInformation("Processor::InternalExecute::Start");
            EnsureConfigured();
            ProcessorEnrichers.ProcessorExecutionBegin(this);
            MigratePipelinesAsync().GetAwaiter().GetResult();
            ProcessorEnrichers.ProcessorExecutionEnd(this);
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
        }

        /// <summary>
        /// Executes Method for migrating Taskgroups, Variablegroups or Pipelines, depinding on what
        /// is set in the config.
        /// </summary>
        private async System.Threading.Tasks.Task MigratePipelinesAsync()
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            IEnumerable<Mapping> serviceConnectionMappings = null;
            IEnumerable<Mapping> taskGroupMappings = null;
            IEnumerable<Mapping> variableGroupMappings = null;
            if (_Options.MigrateServiceConnections)
            {
                serviceConnectionMappings = await CreateServiceConnectionsAsync();
            }
            if (_Options.MigrateVariableGroups)
            {
                variableGroupMappings = await CreateVariableGroupDefinitionsAsync();
            }
            if (_Options.MigrateTaskGroups)
            {
                taskGroupMappings = await CreateTaskGroupDefinitionsAsync();
            }
            if (_Options.MigrateBuildPipelines)
            {
                await CreateBuildPipelinesAsync(taskGroupMappings, variableGroupMappings);
            }

            if (_Options.MigrateReleasePipelines)
            {
                await CreateReleasePipelinesAsync(taskGroupMappings, variableGroupMappings);
            }
            stopwatch.Stop();
            Log.LogDebug("DONE in {Elapsed} ", stopwatch.Elapsed.ToString("c"));
        }

        /// <summary>
        /// Map the taskgroups that are already migrated
        /// </summary>
        /// <typeparam name="DefintionType"></typeparam>
        /// <param name="sourceDefinitions"></param>
        /// <param name="targetDefinitions"></param>
        /// <param name="newMappings"></param>
        /// <returns>Mapping list</returns>
        private IEnumerable<Mapping> FindExistingMappings<DefintionType>(IEnumerable<DefintionType> sourceDefinitions, IEnumerable<DefintionType> targetDefinitions, List<Mapping> newMappings)
            where DefintionType : RestApiDefinition, new()
        {
            // This is not safe, because the target project can have a taskgroup with the same name
            // but with different content To make this save we must add a local storage option for
            // the mappings (sid, tid)
            var alreadyMigratedMappings = new List<Mapping>();
            var alreadyMigratedDefintions = targetDefinitions.Where(t => newMappings.Any(m => m.TargetId == t.Id) == false).ToList();
            foreach (var item in alreadyMigratedDefintions)
            {
                var source = sourceDefinitions.FirstOrDefault(d => d.Name == item.Name);
                if (source == null)
                {
                    Log.LogInformation("The {DefinitionType} {DefinitionName}({DefinitionId}) doesn't exsist in the source collection.", typeof(DefintionType).Name, item.Name, item.Id);
                }
                else
                {
                    alreadyMigratedMappings.Add(new()
                    {
                        SourceId = source.Id,
                        TargetId = item.Id,
                        Name = item.Name
                    });
                }
            }
            return alreadyMigratedMappings;
        }

        /// <summary>
        /// Filter existing Definitions
        /// </summary>
        /// <typeparam name="DefinitionType"></typeparam>
        /// <param name="sourceDefinitions"></param>
        /// <param name="targetDefinitions"></param>
        /// <returns>List of filtered Definitions</returns>
        private IEnumerable<DefinitionType> FilterOutExistingDefinitions<DefinitionType>(IEnumerable<DefinitionType> sourceDefinitions, IEnumerable<DefinitionType> targetDefinitions)
            where DefinitionType : RestApiDefinition, new()
        {
            var objectsToMigrate = sourceDefinitions.Where(s => !targetDefinitions.Any(t => t.Name == s.Name));

            Log.LogInformation("{ObjectsToBeMigrated} of {TotalObjects} source {DefinitionType}(s) are going to be migrated..", objectsToMigrate.Count(), sourceDefinitions.Count(), typeof(DefinitionType).Name);

            return objectsToMigrate;
        }

        /// <summary>
        /// Filter existing TaskGroups
        /// </summary>
        /// <typeparam name="DefinitionType"></typeparam>
        /// <param name="sourceDefinitions"></param>
        /// <param name="targetDefinitions"></param>
        /// <returns>List of filtered Definitions</returns>
        private IEnumerable<TaskGroup> FilterOutExistingTaskGroups(IEnumerable<TaskGroup> sourceDefinitions, IEnumerable<TaskGroup> targetDefinitions)
        {
            var objectsToMigrate = sourceDefinitions.Where(s => !targetDefinitions.Any(t => t.Name == s.Name));
            var rootSourceDefinitions = SortDefinitionsByVersion(objectsToMigrate).First();
            Log.LogInformation("{ObjectsToBeMigrated} of {TotalObjects} source {DefinitionType}(s) are going to be migrated..", objectsToMigrate.GroupBy(o => o.Name).Where(o => o.Count() >= 1).Count(), rootSourceDefinitions.Count(), typeof(TaskGroup).Name);
            return objectsToMigrate;
        }

        /// <summary>
        /// Group and Sort Definitions by Version numer
        /// </summary>
        /// <param name="sourceDefinitions"></param>
        /// <returns>List of sorted Definitions</returns>
        private IEnumerable<IEnumerable<TaskGroup>> SortDefinitionsByVersion(IEnumerable<TaskGroup> sourceDefinitions)
        {
            var groupList = new List<IEnumerable<TaskGroup>>();
            sourceDefinitions.OrderBy(d => d.Version.Major);
            var rootGroups = sourceDefinitions.Where(d => d.Version.Major == 1);
            var updatedGroups = sourceDefinitions.Where(d => d.Version.Major > 1);
            groupList.Add(rootGroups);
            groupList.Add(updatedGroups);

            return groupList;
        }

        private async Task<IEnumerable<Mapping>> CreateBuildPipelinesAsync(IEnumerable<Mapping> TaskGroupMapping = null, IEnumerable<Mapping> VariableGroupMapping = null)
        {
            Log.LogInformation("Processing Build Pipelines..");

            var sourceDefinitions = await Source.GetApiDefinitionsAsync<BuildDefinition>();
            var targetDefinitions = await Target.GetApiDefinitionsAsync<BuildDefinition>();
            var sourceServiceConnections = await Source.GetApiDefinitionsAsync<ServiceConnection>();
            var targetServiceConnections = await Target.GetApiDefinitionsAsync<ServiceConnection>();
            var targetRepos = await Target.GetApiDefinitionsAsync<GitRepo>();
            var definitionsToBeMigrated = FilterOutExistingDefinitions(sourceDefinitions, targetDefinitions);

            definitionsToBeMigrated = FilterAwayIfAnyMapsAreMissing(definitionsToBeMigrated, TaskGroupMapping, VariableGroupMapping);
            // Replace taskgroup and variablegroup sIds with tIds
            foreach (var definitionToBeMigrated in definitionsToBeMigrated)
            {
                var sourceConnectedServiceId = definitionToBeMigrated.Repository.Properties.ConnectedServiceId;
                var targetConnectedServiceId = targetServiceConnections.FirstOrDefault(s => sourceServiceConnections
                    .FirstOrDefault(c => c.Id == sourceConnectedServiceId)?.Name == s.Name)?.Id;
                definitionToBeMigrated.Repository.Properties.ConnectedServiceId = targetConnectedServiceId;

                // Need to ensure that service connections used by tasks are mapped properly.
                if (definitionToBeMigrated.Process != null && definitionToBeMigrated.Process.Phases != null)
                {
                    foreach (var phase in definitionToBeMigrated.Process.Phases)
                    {
                        if (phase.Steps != null)
                        {
                            foreach (var step in phase.Steps)
                            {
                                if (step.Inputs != null && step.Inputs.Any(x => x.Key.Equals("id", StringComparison.OrdinalIgnoreCase)))
                                {
                                    // Inputs are unique in the sense that we don't always know in advance what each endpoint type will need in terms of mapping
                                    var inputs = step.Inputs.ToDictionary(x => x.Key, x => x.Value);

                                    /*
                                    var sourceConnection = sourceServiceConnections.FirstOrDefault(s => s.Id == (step.Inputs as dynamic).id);
                                    var targetConnection = targetServiceConnections.FirstOrDefault(x => x.Id.Equals((step.Inputs as dynamic).id, StringComparison.OrdinalIgnoreCase));

                                    if (targetConnection == null)
                                    {
                                        // Let's try to found the source in the target by name
                                        targetConnection = targetServiceConnections.FirstOrDefault(x => x.Name.Equals(sourceConnection.Name, StringComparison.OrdinalIgnoreCase));
                                    }
                                    if (targetConnection == null)
                                    {
                                        Log.LogWarning($"Could not find source endpoint [{step.DisplayName}::{sourceConnection.Name}] in target.");
                                        continue;
                                    }*/

                                    // Generalized ID mapping - for any input property that looks like the source endpoint's ID
                                    foreach (var input in inputs.ToArray())
                                    {
                                        var sourceEndpoint = sourceServiceConnections.FirstOrDefault(x => x.Id.Equals(input.Value.ToString(), StringComparison.OrdinalIgnoreCase));
                                        if (sourceEndpoint != null)
                                        {
                                            var targetEndpoint = targetServiceConnections.FirstOrDefault(x => x.Name.Equals(sourceEndpoint.Name, StringComparison.OrdinalIgnoreCase));
                                            inputs[input.Key] = targetEndpoint.Id;
                                        }
                                    }

                                    // Custom ID mapping
                                    /*if (inputs.ContainsKey("gitHubConnection"))
                                    {
                                        // Need to map the source GH connection to Target
                                        var sourceGH = sourceServiceConnections.FirstOrDefault(x => x.Id.Equals(inputs["gitHubConnection"].ToString(), StringComparison.OrdinalIgnoreCase));
                                        var targetEndpoint = targetServiceConnections.FirstOrDefault(x => x.Name.Equals(sourceGH.Name, StringComparison.OrdinalIgnoreCase));
                                        inputs["gitHubConnection"] = targetEndpoint?.Id;
                                    }*/

                                    step.Inputs = inputs.ToExpando();
                                }
                            }
                        }
                    }
                }

                if (definitionToBeMigrated.Repository.Type == "TfsGit")
                {
                    var sourceRepoName = definitionToBeMigrated.Repository.Name;
                    var targetRepo = targetRepos.FirstOrDefault(tgt => tgt.Name.Equals(sourceRepoName, StringComparison.OrdinalIgnoreCase));
                    if (targetRepo != null)
                    {
                        definitionToBeMigrated.Repository.Url = new Uri(targetRepo.RemoteUrl);
                        definitionToBeMigrated.Repository.Id = targetRepo.Id;
                    }
                }

                if (TaskGroupMapping is not null && definitionToBeMigrated.Process.Phases != null)
                {
                    foreach (var phase in definitionToBeMigrated.Process.Phases)
                    {
                        foreach (var step in phase.Steps)
                        {
                            if (step.Task.DefinitionType.ToLower() != "metaTask".ToLower())
                            {
                                continue;
                            }
                            var mapping = TaskGroupMapping.FirstOrDefault(d => d.SourceId == step.Task.Id);
                            if (mapping == null)
                            {
                                Log.LogWarning("Can't find taskgroup {MissingTaskGroupId} in the target collection.", step.Task.Id);
                            }
                            else
                            {
                                step.Task.Id = mapping.TargetId;
                            }
                        }
                    }
                }

                if (VariableGroupMapping is not null && definitionToBeMigrated.VariableGroups is not null)
                {
                    foreach (var variableGroup in definitionToBeMigrated.VariableGroups)
                    {
                        if (variableGroup != null)
                        {
                            continue;
                        }
                        var mapping = VariableGroupMapping.FirstOrDefault(d => d.SourceId == variableGroup.Id);
                        if (mapping == null)
                        {
                            Log.LogWarning("Can't find variablegroup {MissingVariableGroupId} in the target collection.", variableGroup.Id);
                        }
                        else
                        {
                            variableGroup.Id = mapping.TargetId;
                        }
                    }
                }
                definitionToBeMigrated.Project = null;
            }
            var mappings = await Target.CreateApiDefinitionsAsync<BuildDefinition>(definitionsToBeMigrated.ToList());
            mappings.AddRange(FindExistingMappings(sourceDefinitions, targetDefinitions, mappings));
            return mappings;
        }

        private async Task<IEnumerable<Mapping>> CreatePoolMappingsAsync<DefinitionType>()
            where DefinitionType : RestApiDefinition, new()
        {
            var sourcePools = await Source.GetApiDefinitionsAsync<DefinitionType>();
            var targetPools = await Target.GetApiDefinitionsAsync<DefinitionType>();
            var mappings = new List<Mapping>();
            foreach (var sourcePool in sourcePools)
            {
                var targetPool = targetPools.FirstOrDefault(t => t.Name == sourcePool.Name);
                if (targetPool is not null)
                {
                    mappings.Add(new()
                    {
                        SourceId = sourcePool.Id,
                        TargetId = targetPool.Id,
                        Name = targetPool.Name
                    });
                }
            }
            return mappings;
        }

        private void UpdateQueueIdForPhase(DeployPhase phase, IEnumerable<Mapping> mappings)
        {
            var mapping = mappings.FirstOrDefault(a => a.SourceId == phase.DeploymentInput.QueueId.ToString());
            if (mapping is not null)
            {
                phase.DeploymentInput.QueueId = int.Parse(mapping.TargetId);
            }
            else
            {
                phase.DeploymentInput.QueueId = 0;
            }
        }

        private async Task<IEnumerable<Mapping>> CreateReleasePipelinesAsync(IEnumerable<Mapping> TaskGroupMapping = null, IEnumerable<Mapping> VariableGroupMapping = null)
        {
            Log.LogInformation($"Processing Release Pipelines..");

            var sourceDefinitions = await Source.GetApiDefinitionsAsync<ReleaseDefinition>();
            var targetDefinitions = await Target.GetApiDefinitionsAsync<ReleaseDefinition>();

            var agentPoolMappings = await CreatePoolMappingsAsync<TaskAgentPool>();
            var deploymentGroupMappings = await CreatePoolMappingsAsync<DeploymentGroup>();

            var definitionsToBeMigrated = FilterOutExistingDefinitions(sourceDefinitions, targetDefinitions);
            if (_Options.ReleasePipelines is not null)
            {
                definitionsToBeMigrated = definitionsToBeMigrated.Where(d => _Options.ReleasePipelines.Contains(d.Name));
            }

            definitionsToBeMigrated = FilterAwayIfAnyMapsAreMissing(definitionsToBeMigrated, TaskGroupMapping, VariableGroupMapping);

            // Replace queue, taskgroup and variablegroup sourceIds with targetIds
            foreach (var definitionToBeMigrated in definitionsToBeMigrated)
            {
                UpdateQueueIdOnPhases(definitionToBeMigrated, agentPoolMappings, deploymentGroupMappings);

                UpdateTaskGroupId(definitionToBeMigrated, TaskGroupMapping);

                if (VariableGroupMapping is not null)
                {
                    UpdateVariableGroupId(definitionToBeMigrated.VariableGroups, VariableGroupMapping);

                    foreach (var environment in definitionToBeMigrated.Environments)
                    {
                        UpdateVariableGroupId(environment.VariableGroups, VariableGroupMapping);
                    }
                }
            }

            var mappings = await Target.CreateApiDefinitionsAsync<ReleaseDefinition>(definitionsToBeMigrated);
            mappings.AddRange(FindExistingMappings(sourceDefinitions, targetDefinitions, mappings));
            return mappings;
        }

        private IEnumerable<DefinitionType> FilterAwayIfAnyMapsAreMissing<DefinitionType>(
                                                IEnumerable<DefinitionType> definitionsToBeMigrated,
                                                IEnumerable<Mapping> TaskGroupMapping,
                                                IEnumerable<Mapping> VariableGroupMapping)
            where DefinitionType : RestApiDefinition
        {
            //filter away definitions that contains task or variable groups if we dont have those mappings
            if (TaskGroupMapping is null)
            {
                var containsTaskGroup = definitionsToBeMigrated.Any(d => d.HasTaskGroups());
                if (containsTaskGroup)
                {
                    Log.LogWarning("You can't migrate pipelines that uses taskgroups if you didn't migrate taskgroups");
                    definitionsToBeMigrated = definitionsToBeMigrated.Where(d => d.HasTaskGroups() == false);
                }
            }
            if (VariableGroupMapping is null)
            {
                var containsVariableGroup = definitionsToBeMigrated.Any(d => d.HasVariableGroups());
                if (containsVariableGroup)
                {
                    Log.LogWarning("You can't migrate pipelines that uses variablegroups if you didn't migrate variablegroups");
                    definitionsToBeMigrated = definitionsToBeMigrated.Where(d => d.HasTaskGroups() == false);
                }
            }

            return definitionsToBeMigrated;
        }

        private void UpdateVariableGroupId(int[] variableGroupIds, IEnumerable<Mapping> VariableGroupMapping)
        {
            for (int i = 0; i < variableGroupIds.Length; i++)
            {
                var oldId = variableGroupIds[i].ToString();
                var mapping = VariableGroupMapping.FirstOrDefault(d => d.SourceId == oldId);
                if (mapping is not null)
                {
                    variableGroupIds[i] = int.Parse(mapping.TargetId);
                }
                else
                {
                    //Not sure if we should exit hard in this case?
                    Log.LogWarning("Can't find variablegroups {OldVariableGroupId} in the target collection.", oldId);
                }
            }
        }

        private void UpdateTaskGroupId(ReleaseDefinition definitionToBeMigrated, IEnumerable<Mapping> TaskGroupMapping)
        {
            if (TaskGroupMapping is null)
            {
                return;
            }
            foreach (var environment in definitionToBeMigrated.Environments)
            {
                foreach (var deployPhase in environment.DeployPhases)
                {
                    foreach (var WorkflowTask in deployPhase.WorkflowTasks)
                    {
                        if (WorkflowTask.DefinitionType != null && WorkflowTask.DefinitionType.ToLower() != "metaTask".ToLower())
                        {
                            continue;
                        }
                        var mapping = TaskGroupMapping.FirstOrDefault(d => d.SourceId == WorkflowTask.TaskId.ToString());
                        if (mapping == null)
                        {
                            Log.LogWarning("Can't find taskgroup {TaskGroupName} in the target collection.", WorkflowTask.Name);
                        }
                        else
                        {
                            WorkflowTask.TaskId = Guid.Parse(mapping.TargetId);
                        }
                    }
                }
            }
        }

        private void UpdateQueueIdOnPhases(ReleaseDefinition definitionToBeMigrated, IEnumerable<Mapping> agentPoolMappings, IEnumerable<Mapping> deploymentGroupMappings)
        {
            foreach (var environment in definitionToBeMigrated.Environments)
            {
                foreach (var phase in environment.DeployPhases)
                {
                    if (phase.PhaseType == "agentBasedDeployment")
                    {
                        UpdateQueueIdForPhase(phase, agentPoolMappings);
                    }
                    else if (phase.PhaseType == "machineGroupBasedDeployment")
                    {
                        UpdateQueueIdForPhase(phase, deploymentGroupMappings);
                    }
                }
            }
        }

        private async Task<IEnumerable<Mapping>> CreateServiceConnectionsAsync()
        {
            Log.LogInformation($"Processing Service Connections..");

            var sourceDefinitions = await Source.GetApiDefinitionsAsync<ServiceConnection>();
            var targetDefinitions = await Target.GetApiDefinitionsAsync<ServiceConnection>();
            var mappings = await Target.CreateApiDefinitionsAsync(FilterOutExistingDefinitions(sourceDefinitions, targetDefinitions));
            mappings.AddRange(FindExistingMappings(sourceDefinitions, targetDefinitions, mappings));
            return mappings;
        }

        private async Task<IEnumerable<Mapping>> CreateTaskGroupDefinitionsAsync()
        {
            Log.LogInformation($"Processing Taskgroups..");

            var sourceDefinitions = await Source.GetApiDefinitionsAsync<TaskGroup>();
            var targetDefinitions = await Target.GetApiDefinitionsAsync<TaskGroup>();
            var filteredTaskGroups = FilterOutExistingTaskGroups(sourceDefinitions, targetDefinitions);
            var rootSourceDefinitions = SortDefinitionsByVersion(filteredTaskGroups).First();
            var updatedSourceDefinitions = SortDefinitionsByVersion(filteredTaskGroups).Last();

            var mappings = await Target.CreateApiDefinitionsAsync(rootSourceDefinitions);

            targetDefinitions = await Target.GetApiDefinitionsAsync<TaskGroup>();
            var rootTargetDefinitions = SortDefinitionsByVersion(targetDefinitions).First();
            await Target.UpdateTaskGroupsAsync(targetDefinitions, rootTargetDefinitions, updatedSourceDefinitions);

            targetDefinitions = await Target.GetApiDefinitionsAsync<TaskGroup>();
            mappings.AddRange(FindExistingMappings(sourceDefinitions, targetDefinitions.Where(d => d.Name != null), mappings));
            return mappings;
        }

        private async Task<IEnumerable<Mapping>> CreateVariableGroupDefinitionsAsync()
        {
            Log.LogInformation($"Processing Variablegroups..");

            var sourceDefinitions = await Source.GetApiDefinitionsAsync<VariableGroups>();
            var targetDefinitions = await Target.GetApiDefinitionsAsync<VariableGroups>();
            var filteredDefinition = FilterOutExistingDefinitions(sourceDefinitions, targetDefinitions);
            foreach (var variableGroup in filteredDefinition)
            {
                //was needed when now trying to migrated to azure devops services
                variableGroup.VariableGroupProjectReferences = new VariableGroupProjectReference[1];
                variableGroup.VariableGroupProjectReferences[0] = new VariableGroupProjectReference
                {
                    Name = variableGroup.Name,
                    ProjectReference = new ProjectReference
                    {
                        Name = Target.Options.Project
                    }
                };
            }
            var mappings = await Target.CreateApiDefinitionsAsync(filteredDefinition);
            mappings.AddRange(FindExistingMappings(sourceDefinitions, targetDefinitions, mappings));
            return mappings;
        }
    }
    public static class DictionaryExtensionMethods
    {
        /// <summary>
        /// Extension method that turns a dictionary of string and object to an ExpandoObject
        /// </summary>
        public static ExpandoObject ToExpando(this IDictionary<string, object> dictionary)
        {
            var expando = new ExpandoObject();
            var expandoDic = (IDictionary<string, object>)expando;

            // go through the items in the dictionary and copy over the key value pairs)
            foreach (var kvp in dictionary)
            {
                // if the value can also be turned into an ExpandoObject, then do it!
                if (kvp.Value is IDictionary<string, object>)
                {
                    var expandoValue = ((IDictionary<string, object>)kvp.Value).ToExpando();
                    expandoDic.Add(kvp.Key, expandoValue);
                }
                else if (kvp.Value is ICollection)
                {
                    // iterate through the collection and convert any strin-object dictionaries
                    // along the way into expando objects
                    var itemList = new List<object>();
                    foreach (var item in (ICollection)kvp.Value)
                    {
                        if (item is IDictionary<string, object>)
                        {
                            var expandoItem = ((IDictionary<string, object>)item).ToExpando();
                            itemList.Add(expandoItem);
                        }
                        else
                        {
                            itemList.Add(item);
                        }
                    }

                    expandoDic.Add(kvp.Key, itemList);
                }
                else
                {
                    expandoDic.Add(kvp);
                }
            }

            return expando;
        }
    }
}