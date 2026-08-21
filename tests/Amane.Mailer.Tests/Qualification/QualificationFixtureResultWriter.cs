using System.Text.Json;

namespace Amane.Mailer.Tests.Qualification;

internal static class QualificationFixtureResultWriter
{
    internal static bool WriteIfRequested(
        Type fixtureType,
        string methodName,
        string fixtureId,
        string scenarioId,
        string variantId,
        bool passed,
        IReadOnlyDictionary<string, object> observations)
    {
        var outputPath = Environment.GetEnvironmentVariable("AMANE_QUALIFICATION_FIXTURE_RESULT_PATH");
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return passed;
        }

        var fixtureResult = new
        {
            schemaVersion = 1,
            kind = "qualification-fixture-result",
            fixtureId,
            fixtureRevision = "1",
            scenarioId,
            variantId,
            sourceTestId = $"{fixtureType.FullName}.{methodName}",
            result = passed ? "PASS" : "FAIL",
            operationExitCode = passed ? 0 : 1,
            observations,
        };

        File.WriteAllText(
            outputPath,
            JsonSerializer.Serialize(fixtureResult, new JsonSerializerOptions { WriteIndented = false }));
        return passed;
    }
}
