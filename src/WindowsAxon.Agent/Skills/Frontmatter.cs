using YamlDotNet.Serialization;

namespace WindowsAxon.Agent.Skills;

public sealed partial class SkillCatalog
{
    private sealed class Frontmatter
    {
        [YamlMember(Alias = "name")]
        public string? Name
        {
            get; set;
        }

        [YamlMember(Alias = "description")]
        public string? Description
        {
            get; set;
        }

        [YamlMember(Alias = "compatibility")]
        public string? Compatibility
        {
            get; set;
        }
    }
}
