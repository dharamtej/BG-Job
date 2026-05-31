using CareerPanda.DataAccess.Util;
using Xunit;

namespace CareerPanda.Tests.Util;

public class JobEnricherTests
{
    // A small stand-in for the md.skills vocabulary, including the punctuation-heavy
    // names that are the hard part of extraction.
    private static readonly string[] Vocab =
    {
        "Python", "Java", "JavaScript", "AWS", "React", "SQL", "Kubernetes", "Go",
        "Machine Learning", "Project Management", "Communication",
        "C#", "C++", "Node.js", "CI/CD", ".NET", "ASP.NET"
    };

    private static readonly JobEnricher.SkillIndex Index = JobEnricher.BuildSkillIndex(Vocab);

    [Fact]
    public void ExtractSkills_FindsWordAndPhraseSkills()
    {
        var desc = "We need a Senior Engineer skilled in Python and AWS. " +
                   "Experience with React and Kubernetes required. Machine learning a plus.";
        var skills = JobEnricher.ExtractSkills("Senior Engineer", desc, null, Index);

        Assert.NotNull(skills);
        Assert.Contains("Python", skills!);
        Assert.Contains("AWS", skills);
        Assert.Contains("React", skills);
        Assert.Contains("Kubernetes", skills);
        Assert.Contains("Machine Learning", skills);   // phrase, canonical casing
        Assert.DoesNotContain("Java", skills);          // "Java" must not match inside "JavaScript"/absent
        Assert.DoesNotContain("Go", skills);            // word "go" absent
    }

    [Fact]
    public void ExtractSkills_HandlesPunctuationSkills()
    {
        var desc = "Backend in C# and Node.js with CI/CD pipelines. C++ exposure helpful.";
        var skills = JobEnricher.ExtractSkills(null, desc, null, Index);

        Assert.NotNull(skills);
        Assert.Contains("C#", skills!);
        Assert.Contains("Node.js", skills);
        Assert.Contains("CI/CD", skills);
        Assert.Contains("C++", skills);
        Assert.DoesNotContain(".NET", skills);   // ".net" not present; "node.js" must not bleed into it
        Assert.DoesNotContain("ASP.NET", skills);
    }

    [Fact]
    public void ExtractSkills_CanonicalizesExistingTagsAndDropsNonVocab()
    {
        // Department label a handler may have stuffed into skills should be dropped;
        // a real in-vocabulary tag should be kept with canonical casing.
        var existing = new[] { "Engineering", "javascript" };
        var skills = JobEnricher.ExtractSkills("Engineer", "No skills named here.", existing, Index);

        Assert.NotNull(skills);
        Assert.Contains("JavaScript", skills!);     // canonicalized from "javascript"
        Assert.DoesNotContain("Engineering", skills); // not in md.skills → dropped
    }

    [Fact]
    public void ExtractSkills_ReturnsNullWhenNothingMatches()
    {
        var skills = JobEnricher.ExtractSkills("Barista", "Make coffee and greet guests.", null, Index);
        Assert.Null(skills);
    }

    [Theory]
    [InlineData("Requires 5+ years of experience.", 5, null)]
    [InlineData("Looking for 3-5 years of experience.", 3, 5)]
    [InlineData("Minimum of 7 years in software.", 7, null)]
    [InlineData("At least 2 years required.", 2, null)]
    [InlineData("Entry level, no experience needed.", null, null)]
    public void ExtractExperience_ParsesYears(string text, int? expectedMin, int? expectedMax)
    {
        var (min, max) = JobEnricher.ExtractExperience(text);
        Assert.Equal(expectedMin, min);
        Assert.Equal(expectedMax, max);
    }

    [Theory]
    [InlineData("PhD in Computer Science preferred.", "Doctorate")]
    [InlineData("Master's degree or higher.", "Master's")]
    [InlineData("Bachelor's degree in Engineering required.", "Bachelor's")]
    [InlineData("High school diploma or equivalent.", "High School/Diploma")]
    [InlineData("No formal education listed.", null)]
    public void ExtractEducation_ReturnsHighestLevel(string text, string? expected)
    {
        Assert.Equal(expected, JobEnricher.ExtractEducation(text));
    }
}
