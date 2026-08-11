using System.Text.Json;
using System.Globalization;
using Xunit;

namespace Scheduler.Infrastructure.FormalVerification.Tests;

public sealed class FormalFixtureIntegrityTests
{
    [Theory]
    [InlineData("personal_selector_tiny_unsat", "tiny_unsat")]
    [InlineData("personal_selector_branch_unsat", "branch_unsat")]
    public void StaticUnsatFixtureMetadataMatchesItsDimacsFormula(string directoryName, string fixtureName)
    {
        var fixtureDirectory = Path.Combine(FindWorkspaceRoot(), "scheduler", "formal", "tests", "test_vectors", directoryName);
        var formulaPath = Path.Combine(fixtureDirectory, $"{fixtureName}.cnf");
        var clausesPath = Path.Combine(fixtureDirectory, $"{fixtureName}.clauses.json");
        var variableMapPath = Path.Combine(fixtureDirectory, $"{fixtureName}.varmap.json");

        var (variableCount, clauses) = ParseDimacs(formulaPath);
        using var clausesDocument = JsonDocument.Parse(File.ReadAllText(clausesPath));
        using var variableMapDocument = JsonDocument.Parse(File.ReadAllText(variableMapPath));

        var clausesRoot = clausesDocument.RootElement;
        Assert.Equal(variableCount, clausesRoot.GetProperty("variable_count").GetInt32());
        Assert.Equal(clauses.Count, clausesRoot.GetProperty("clause_count").GetInt32());
        Assert.Equal(clauses.Count, clausesRoot.GetProperty("clauses").GetArrayLength());

        foreach (var (clause, index) in clauses.Select((value, index) => (value, index)))
        {
            var metadataClause = clausesRoot.GetProperty("clauses")[index];
            Assert.Equal(index + 1, metadataClause.GetProperty("clause_id").GetInt32());
            Assert.Equal(clause, metadataClause.GetProperty("literals").EnumerateArray().Select(value => value.GetInt32()));
        }

        var variableMap = variableMapDocument.RootElement;
        Assert.Equal(variableCount, variableMap.GetProperty("variable_count").GetInt32());
        Assert.Equal(variableCount, variableMap.GetProperty("variables").GetArrayLength());
        foreach (var variable in variableMap.GetProperty("variables").EnumerateArray())
        {
            Assert.InRange(variable.GetProperty("var").GetInt32(), 1, variableCount);
        }
    }

    private static (int VariableCount, List<int[]> Clauses) ParseDimacs(string path)
    {
        var header = default(string);
        var clauses = new List<int[]>();
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('c'))
            {
                continue;
            }

            if (line.StartsWith("p cnf ", StringComparison.Ordinal))
            {
                header = line;
                continue;
            }

            var literals = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => int.Parse(value, CultureInfo.InvariantCulture))
                .ToArray();
            Assert.NotEmpty(literals);
            Assert.Equal(0, literals[^1]);
            Assert.DoesNotContain(0, literals[..^1]);
            clauses.Add(literals[..^1]);
        }

        Assert.NotNull(header);
        var headerParts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var variableCount = int.Parse(headerParts[2], CultureInfo.InvariantCulture);
        var declaredClauseCount = int.Parse(headerParts[3], CultureInfo.InvariantCulture);
        Assert.Equal(declaredClauseCount, clauses.Count);
        Assert.All(clauses.SelectMany(clause => clause), literal => Assert.InRange(Math.Abs(literal), 1, variableCount));
        return (variableCount, clauses);
    }

    private static string FindWorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "scheduler", "formal", "tests", "test_vectors")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Unable to locate formal test vectors from the test output directory.");
    }
}
