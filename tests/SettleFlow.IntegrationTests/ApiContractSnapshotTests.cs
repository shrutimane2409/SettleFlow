using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace SettleFlow.IntegrationTests;

/// <summary>
/// The OpenAPI document is the contract with the dashboard and with merchants. It's committed as
/// frontend/openapi.json; the frontend generates TypeScript types from it and fails to compile if its
/// own types drift. After an intentional API change: SETTLEFLOW_UPDATE_SNAPSHOTS=1 dotnet test, then
/// `npm run gen:api` in frontend/.
/// </summary>
[Collection(PipelineCollection.Name)]
public sealed class ApiContractSnapshotTests(SettleFlowFixture f)
{
    [Fact]
    public async Task OpenApi_document_matches_the_committed_contract()
    {
        var current = JsonNode.Parse(await f.AnonymousClient().GetStringAsync("/swagger/v1/swagger.json"))!;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SettleFlow.sln"))) dir = dir.Parent;
        var path = Path.Combine(dir!.FullName, "frontend", "openapi.json");

        if (Environment.GetEnvironmentVariable("SETTLEFLOW_UPDATE_SNAPSHOTS") == "1" || (!File.Exists(path) && Environment.GetEnvironmentVariable("CI") is null))
        {
            await File.WriteAllTextAsync(path, current.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            return;
        }

        JsonNode.DeepEquals(JsonNode.Parse(await File.ReadAllTextAsync(path)), current).Should().BeTrue(
            "the API contract changed. If intentional: SETTLEFLOW_UPDATE_SNAPSHOTS=1 dotnet test, then `npm run gen:api` in frontend/");
    }
}
