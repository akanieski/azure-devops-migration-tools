using System;
using System.Collections.Generic;
using System.Text;

namespace MigrationTools.DataContracts.Repos
{
    [ApiPath("git/repositories", IncludeProject: true, IncludeTrailingSlash: false, ApiVersion: "6.0")]
    [ApiName("Git Repositories")]
    public partial class GitRepo : BaseGitDefinition
    {
        public string Url { get; set; }
        public string DefaultBranch { get; set; }
        public int Size { get; set; }
        public string RemoteUrl { get; set; }
        public string SshUrl { get; set; }
        public string WebUrl { get; set; }
        public bool IsDisabled { get; set; }
    }
    public abstract class BaseGitDefinition : RestApiDefinition
    {
        public override bool HasTaskGroups()
        {
            throw new NotImplementedException();
        }

        public override bool HasVariableGroups()
        {
            throw new NotImplementedException();
        }

        public override void ResetObject()
        {
            throw new NotImplementedException();
        }
    }
}
