using WindowsAxon.Windows;

namespace WindowsAxon.Agent.Tests.Tools;

public class SkillsFolderGuardTests
{
    private const string Skills = @"C:\Data\WindowsAxon\skills";

    [Theory]
    [InlineData(@"C:\Data\WindowsAxon\skills")]
    [InlineData(@"C:\Data\WindowsAxon\Skills\evil\SKILL.md")]
    [InlineData(@"C:\Data\WindowsAxon\skills\..\skills\evil")]
    [InlineData(@"C:\Data\WindowsAxon")]
    public void Check_rejects_the_skills_folder_its_content_and_its_parents(string path) =>
        Assert.Throws<UnauthorizedAccessException>(() => SkillsFolderGuard.Check(path, Skills));

    [Theory]
    [InlineData(@"C:\Data\WindowsAxon\skills-old\a.txt")]
    [InlineData(@"C:\Data\WindowsAxon\logs\a.txt")]
    public void Check_allows_other_paths(string path) => Assert.Equal(path, SkillsFolderGuard.Check(path, Skills));

    [Fact]
    public void Copy_into_the_skills_folder_is_refused()
    {
        var data = Directory.CreateTempSubdirectory("windowsaxon-data-").FullName;
        var skills = Directory.CreateDirectory(Path.Combine(data, "skills")).FullName;
        var source = Path.Combine(data, "SKILL.md");
        File.WriteAllText(source, "---\nname: evil\n---\n");
        var tools = new FileTools(new Agent.Undo.UndoJournal(Path.Combine(data, "undo"), TimeProvider.System), "test", skills);

        Assert.Throws<UnauthorizedAccessException>(() => tools.Copy(source, Path.Combine(skills, "SKILL.md")));
        Assert.Throws<UnauthorizedAccessException>(() => tools.Create(Path.Combine(skills, "evil")));
        Assert.False(File.Exists(Path.Combine(skills, "SKILL.md")));
    }
}
