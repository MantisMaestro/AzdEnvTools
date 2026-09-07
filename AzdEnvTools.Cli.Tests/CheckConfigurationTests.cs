using System.Net;
using System.Text.Json.Nodes;
using AzdEnvTools.Cli;
using Xunit;

namespace AzdEnvTools.Cli.Tests;

public sealed class CheckConfigurationTests
{
    [Fact]
    public async Task GenericCheckPreservesSettingsButOverridesResourceAndStripsMetadata()
    {
        using var handler = CliTests.EnvironmentAndCheckReplies();
        using var http = new HttpClient(handler);
        var client = new AzureDevOpsClient(http, "my-org", "my-project", "fake-token");
        var configuration = JsonNode.Parse("""
            {
              "id": 123,
              "createdBy": {"id":"someone"},
              "type": {"id":"fe1de3ee-a436-41b4-bb20-f6eb4cb879a7","name":"Task Check"},
              "settings": {
                "displayName":"External validation",
                "definitionRef":{"id":"537fdb7a-a601-4537-aa70-92645a2b5ce4","version":"1.*.*"},
                "inputs":{"method":"POST","body":"{\"nested\":true}"},
                "retryInterval":5
              },
              "timeout":60,
              "resource":{"type":"queue","id":"999","name":"wrong"}
            }
            """)!.AsObject();
        var original = configuration.DeepClone();
        var result = await client.AddCheckAsync(42, configuration, CancellationToken.None);

        Assert.Equal(99, result["id"]!.GetValue<int>());
        var body = handler.Requests[1].Body!;
        Assert.Equal(4, body.Count);
        Assert.True(JsonNode.DeepEquals(configuration["type"], body["type"]));
        Assert.True(JsonNode.DeepEquals(configuration["settings"], body["settings"]));
        Assert.Equal("42", body["resource"]!["id"]!.GetValue<string>());
        Assert.Equal("environment", body["resource"]!["type"]!.GetValue<string>());
        Assert.Equal("production", body["resource"]!["name"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(original, configuration));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"type\":{\"id\":\"not-a-guid\"},\"settings\":{},\"timeout\":60}")]
    [InlineData("{\"type\":{\"id\":\"00000000-0000-0000-0000-000000000000\"},\"settings\":{},\"timeout\":60}")]
    [InlineData("{\"type\":{\"id\":42},\"settings\":{},\"timeout\":60}")]
    [InlineData("{\"type\":{\"id\":\"8c6f20a7-a545-4486-9777-f762fafe0d4d\"},\"settings\":[] ,\"timeout\":60}")]
    [InlineData("{\"type\":{\"id\":\"8c6f20a7-a545-4486-9777-f762fafe0d4d\"},\"timeout\":60}")]
    [InlineData("{\"type\":{\"id\":\"8c6f20a7-a545-4486-9777-f762fafe0d4d\"},\"settings\":{}}")]
    [InlineData("{\"type\":{\"id\":\"8c6f20a7-a545-4486-9777-f762fafe0d4d\"},\"settings\":{},\"timeout\":0}")]
    [InlineData("{\"type\":{\"id\":\"8c6f20a7-a545-4486-9777-f762fafe0d4d\"},\"settings\":{},\"timeout\":\"60\"}")]
    public async Task InvalidGenericConfigurationIsRejectedBeforeHttp(string json)
    {
        using var handler = new StubHandler();
        using var http = new HttpClient(handler);
        var client = new AzureDevOpsClient(http, "org", "project", "fake-token");
        await Assert.ThrowsAsync<ArgumentException>(() => client.AddCheckAsync(42, JsonNode.Parse(json)!.AsObject(), CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task EnvironmentLookupFailureDoesNotCreateCheck()
    {
        using var handler = new StubHandler(CliTests.Reply("""{"message":"Missing environment"}""", HttpStatusCode.NotFound));
        var result = await CliTests.Run(handler, ["approvals", "add", "--environment-id", "42",
            "--approver", "3b3db741-9d03-4e32-a7c0-6c3dfc2013c1"]);
        Assert.Equal(1, result.Code);
        Assert.Equal(HttpMethod.Get, Assert.Single(handler.Requests).Method);
    }

    [Fact]
    public async Task ChecksAddReadsJsonFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, ApprovalConfiguration.Create(
                [Guid.Parse("3b3db741-9d03-4e32-a7c0-6c3dfc2013c1")], 1, "anyOrder", "Review", false, 60).ToJsonString());
            using var handler = CliTests.EnvironmentAndCheckReplies();
            var result = await CliTests.Run(handler, ["checks", "add", "--environment-id", "42", "--file", path]);
            Assert.Equal(0, result.Code);
            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal("Review", handler.Requests[1].Body!["settings"]!["instructions"]!.GetValue<string>());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task MalformedFileFailsWithoutHttp(string json)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, json);
            using var handler = new StubHandler();
            var result = await CliTests.Run(handler, ["checks", "add", "--environment-id", "42", "--file", path]);
            Assert.Equal(1, result.Code);
            Assert.NotEmpty(result.Error);
            Assert.Empty(handler.Requests);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MissingFileFailsWithoutHttp()
    {
        using var handler = new StubHandler();
        var result = await CliTests.Run(handler, ["checks", "add", "--environment-id", "42", "--file",
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json")]);
        Assert.Equal(1, result.Code);
        Assert.NotEmpty(result.Error);
        Assert.Empty(handler.Requests);
    }
}
