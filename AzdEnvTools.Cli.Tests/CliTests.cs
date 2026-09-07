using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AzdEnvTools.Cli;
using Xunit;

namespace AzdEnvTools.Cli.Tests;

public sealed class CliTests
{
    private const string FirstApprover = "3b3db741-9d03-4e32-a7c0-6c3dfc2013c1";
    private const string SecondApprover = "a9487c43-4c5c-43bc-a54d-e61cbb32efd7";
    private const string Pat = "test-pat-not-a-real-secret";

    [Fact]
    public async Task ListUsesPatAndEscapedProjectAndFilterAndFollowsPagination()
    {
        using var handler = new StubHandler(
            Reply("""{"count":1,"value":[{"id":1,"name":"prod"}]}""", continuation: "next +/=&"),
            Reply("""{"count":1,"value":[{"id":2,"name":"prod-east"}]}"""));
        var result = await Run(handler, ["environments", "list", "--name", "prod & east", "--project", "Project A & B"]);

        Assert.Equal(0, result.Code);
        Assert.Empty(result.Error);
        Assert.Equal(2, JsonNode.Parse(result.Output)!.AsArray().Count);
        Assert.Equal(2, handler.Requests.Count);
        var request = handler.Requests[0];
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + Pat)), request.Authorization);
        Assert.Equal("application/json", request.Accept);
        Assert.Equal("https://dev.azure.com/my-org/Project%20A%20%26%20B/_apis/pipelines/environments?api-version=7.2-preview.1&name=prod%20%26%20east", request.Url);
        Assert.Contains("&continuationToken=next%20%2B%2F%3D%26", handler.Requests[1].Url);
        Assert.Contains("&name=prod%20%26%20east", handler.Requests[1].Url);
    }

    [Fact]
    public async Task EmptyListIsAnEmptyJsonArray()
    {
        using var handler = new StubHandler(Reply("""{"count":0,"value":[]}"""));
        var result = await Run(handler, ["environments", "list"]);
        Assert.Equal(0, result.Code);
        Assert.Empty(JsonNode.Parse(result.Output)!.AsArray());
    }

    [Fact]
    public async Task PaginationContinuesEvenWhenPageIsEmpty()
    {
        using var handler = new StubHandler(
            Reply("""{"value":[]}""", continuation: "2"), Reply("""{"value":[{"id":2}]}"""));
        var result = await Run(handler, ["environments", "list"]);
        Assert.Equal(0, result.Code);
        Assert.Single(JsonNode.Parse(result.Output)!.AsArray());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task RepeatedPaginationTokenFailsInsteadOfLoopingOrReturningPartialResults()
    {
        using var handler = new StubHandler(
            Reply("""{"value":[{"id":1}]}""", continuation: "same"),
            Reply("""{"value":[{"id":2}]}""", continuation: "same"));
        var result = await Run(handler, ["environments", "list"]);
        Assert.Equal(1, result.Code);
        Assert.Empty(result.Output);
        Assert.Contains("repeated continuation token", result.Error);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ExplicitConnectionOptionsOverrideEnvironmentVariables()
    {
        using var handler = new StubHandler(Reply("""{"id":42,"name":"prod"}"""));
        var result = await Run(handler, ["--org", "https://dev.azure.com/another-org/", "environments", "get",
            "--id", "42", "--project", "another-project", "--pat", "explicit-pat"]);

        Assert.Equal(0, result.Code);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://dev.azure.com/another-org/another-project/_apis/pipelines/environments/42?api-version=7.2-preview.1", request.Url);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(":explicit-pat")), request.Authorization);
    }

    [Theory]
    [InlineData("http://dev.azure.com/org")]
    [InlineData("https://attacker.example/org")]
    [InlineData("https://dev.azure.com/org/project")]
    [InlineData("https://dev.azure.com/org?anything=1")]
    [InlineData("https://user:pass@dev.azure.com/org")]
    [InlineData("https://dev.azure.com:8443/org")]
    [InlineData("https://dev.azure.com/")]
    [InlineData("../org")]
    public async Task InvalidOrganizationNeverSendsPat(string organization)
    {
        using var handler = new StubHandler();
        var result = await Run(handler, ["environments", "list", "--organization", organization]);
        Assert.Equal(1, result.Code);
        Assert.Empty(handler.Requests);
        Assert.DoesNotContain(Pat, result.Error);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("--version")]
    public async Task HelpAndVersionDoNotNeedCredentials(string argument)
    {
        using var handler = new StubHandler();
        var result = await Run(handler, [argument], _ => null);
        Assert.Equal(0, result.Code);
        Assert.NotEmpty(result.Output);
        Assert.Empty(result.Error);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("AZURE_DEVOPS_PAT")]
    [InlineData("AZURE_DEVOPS_ORGANIZATION")]
    [InlineData("AZURE_DEVOPS_PROJECT")]
    public async Task MissingConnectionSettingHasActionableError(string missing)
    {
        using var handler = new StubHandler();
        var result = await Run(handler, ["environments", "list"], name => name == missing ? null : Settings(name));
        Assert.Equal(1, result.Code);
        Assert.Contains(missing, result.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CreatePostsNameAndDescription()
    {
        using var handler = new StubHandler(Reply("""{"id":42,"name":"production"}"""));
        var result = await Run(handler, ["environments", "create", "--name", "production", "--description", "Deploy here"]);
        Assert.Equal(0, result.Code);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("application/json; charset=utf-8", request.ContentType);
        Assert.EndsWith("/environments?api-version=7.2-preview.1", request.Url);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"name":"production","description":"Deploy here"}"""), request.Body));
    }

    [Fact]
    public async Task CreateOmitsUnspecifiedDescription()
    {
        using var handler = new StubHandler(Reply("""{"id":42}"""));
        var result = await Run(handler, ["environments", "create", "--name", "production"]);
        Assert.Equal(0, result.Code);
        Assert.False(Assert.Single(handler.Requests).Body!.ContainsKey("description"));
    }

    [Theory]
    [InlineData("--description", "", "description")]
    [InlineData("--description", "Updated", "description")]
    [InlineData("--name", "renamed", "name")]
    public async Task UpdatePatchesOnlySpecifiedField(string option, string value, string field)
    {
        using var handler = new StubHandler(Reply("""{"id":42}"""));
        var result = await Run(handler, ["environments", "update", "--id", "42", option, value]);
        Assert.Equal(0, result.Code);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.EndsWith("/environments/42?api-version=7.2-preview.1", request.Url);
        Assert.Single(request.Body!);
        Assert.Equal(value, request.Body![field]!.GetValue<string>());
    }

    [Fact]
    public async Task UpdateCanChangeBothFields()
    {
        using var handler = new StubHandler(Reply("""{"id":42}"""));
        var result = await Run(handler, ["environments", "update", "--id", "42", "--name", "new-name", "--description", "new-description"]);
        Assert.Equal(0, result.Code);
        Assert.Equal(2, Assert.Single(handler.Requests).Body!.Count);
    }

    [Theory]
    [InlineData("environments update --id 42")]
    [InlineData("environments get --id 0")]
    [InlineData("environments get --id -1")]
    [InlineData("environments get --id nope")]
    [InlineData("environments create")]
    [InlineData("environments list --unknown")]
    [InlineData("checks add --environment-id 42")]
    [InlineData("approvals add --environment-id 42")]
    [InlineData("approvals add --environment-id 42 --approver person@example.com")]
    public async Task InvalidCommandFailsBeforeHttp(string command)
    {
        using var handler = new StubHandler();
        var result = await Run(handler, command.Split(' '));
        Assert.Equal(1, result.Code);
        Assert.NotEmpty(result.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task EmptyEnvironmentNameFailsBeforeHttp()
    {
        using var handler = new StubHandler();
        var result = await Run(handler, ["environments", "create", "--name", " "]);
        Assert.Equal(1, result.Code);
        Assert.Contains("--name must not be empty", result.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ApprovalDefaultsRequireAllApproversAndBlockRequester()
    {
        using var handler = EnvironmentAndCheckReplies();
        var result = await Run(handler, ["approvals", "add", "--environment-id", "42",
            "--approver", FirstApprover, "--approver", SecondApprover]);
        Assert.Equal(0, result.Code);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        var request = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith("/checks/configurations?api-version=7.2-preview.1", request.Url);
        var body = request.Body!;
        Assert.Equal(ApprovalConfiguration.TypeId, body["type"]!["id"]!.GetValue<string>());
        Assert.Equal(43200, body["timeout"]!.GetValue<int>());
        Assert.Equal("42", body["resource"]!["id"]!.GetValue<string>());
        Assert.Equal("environment", body["resource"]!["type"]!.GetValue<string>());
        Assert.Equal("production", body["resource"]!["name"]!.GetValue<string>());
        var settings = body["settings"]!;
        Assert.Equal(2, settings["minRequiredApprovers"]!.GetValue<int>());
        Assert.True(settings["requesterCannotBeApprover"]!.GetValue<bool>());
        Assert.Equal("anyOrder", settings["executionOrder"]!.GetValue<string>());
        Assert.Equal(new[] { FirstApprover, SecondApprover }, settings["approvers"]!.AsArray().Select(a => a!["id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ApprovalOptionsAreReflectedInPayload()
    {
        using var handler = EnvironmentAndCheckReplies();
        var result = await Run(handler, ["approvals", "add", "--environment-id", "42",
            "--approver", FirstApprover, SecondApprover, "--minimum-approvers", "1", "--execution-order", "inSequence",
            "--allow-self-approval", "--timeout", "120", "--instructions", "Review release"]);
        Assert.Equal(0, result.Code);
        var body = handler.Requests[1].Body!;
        Assert.Equal(120, body["timeout"]!.GetValue<int>());
        var settings = body["settings"]!;
        Assert.Equal(1, settings["minRequiredApprovers"]!.GetValue<int>());
        Assert.False(settings["requesterCannotBeApprover"]!.GetValue<bool>());
        Assert.Equal("inSequence", settings["executionOrder"]!.GetValue<string>());
        Assert.Equal("Review release", settings["instructions"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("--minimum-approvers", "0")]
    [InlineData("--minimum-approvers", "2")]
    [InlineData("--timeout", "0")]
    [InlineData("--timeout", "43201")]
    [InlineData("--timeout", "nope")]
    [InlineData("--minimum-approvers", "nope")]
    [InlineData("--execution-order", "random")]
    [InlineData("--approver", FirstApprover)]
    [InlineData("--approver", "00000000-0000-0000-0000-000000000000")]
    public async Task InvalidApprovalFailsBeforeHttp(string option, string value)
    {
        using var handler = new StubHandler();
        var result = await Run(handler, ["approvals", "add", "--environment-id", "42", "--approver", FirstApprover, option, value]);
        Assert.Equal(1, result.Code);
        Assert.NotEmpty(result.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ListChecksScopesToEnvironmentAndExpandsSettings()
    {
        using var handler = new StubHandler(Reply("""{"value":[{"id":10,"settings":{"instructions":"Review"}}]}"""));
        var result = await Run(handler, ["checks", "list", "--environment-id", "42"]);
        Assert.Equal(0, result.Code);
        Assert.EndsWith("/checks/configurations?api-version=7.2-preview.1&resourceType=environment&resourceId=42&$expand=settings", Assert.Single(handler.Requests).Url);
        Assert.Equal("Review", JsonNode.Parse(result.Output)![0]!["settings"]!["instructions"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(401, "PAT is valid")]
    [InlineData(403, "PAT scopes")]
    [InlineData(404, "resource ID")]
    [InlineData(429, "rate limiting")]
    [InlineData(500, "not retried")]
    [InlineData(302, "Redirect refused")]
    public async Task ApiErrorsAreActionableAndNeverRetried(int status, string expected)
    {
        using var handler = new StubHandler(Reply("""{"message":"Server detail"}""", (HttpStatusCode)status));
        var result = await Run(handler, ["environments", "create", "--name", "prod"]);
        Assert.Equal(1, result.Code);
        Assert.Empty(result.Output);
        Assert.Contains($"HTTP {status}", result.Error);
        Assert.Contains(expected, result.Error);
        Assert.Contains("Server detail", result.Error);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ApiErrorsRedactPatAndEncodedAuthorization()
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + Pat));
        using var handler = new StubHandler(Reply(new JsonObject { ["message"] = $"{Pat} {encoded}" }.ToJsonString(), HttpStatusCode.BadRequest));
        var result = await Run(handler, ["environments", "list"]);
        Assert.Equal(1, result.Code);
        Assert.DoesNotContain(Pat, result.Error);
        Assert.DoesNotContain(encoded, result.Error);
        Assert.Contains("[REDACTED]", result.Error);
    }

    [Theory]
    [InlineData("<html>Sign in</html>")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"count\":0}")]
    public async Task InvalidSuccessResponseDoesNotMasqueradeAsAnEmptyList(string response)
    {
        using var handler = new StubHandler(Reply(response));
        var result = await Run(handler, ["environments", "list"]);
        Assert.Equal(1, result.Code);
        Assert.Empty(result.Output);
        Assert.NotEmpty(result.Error);
        Assert.DoesNotContain("<html>", result.Error);
    }

    [Fact]
    public async Task NonJsonErrorDoesNotPrintRawHtml()
    {
        using var handler = new StubHandler(Reply("<html>Sign in</html>", HttpStatusCode.Unauthorized));
        var result = await Run(handler, ["environments", "list"]);
        Assert.Equal(1, result.Code);
        Assert.Contains("HTTP 401", result.Error);
        Assert.DoesNotContain("<html>", result.Error);
    }

    [Fact]
    public async Task CancellationReturnsFailureWithWriteSafetyWarning()
    {
        using var handler = new StubHandler();
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await CliApplication.RunAsync(["environments", "create", "--name", "prod"], http, Settings, output, error, cancellation.Token);
        Assert.Equal(1, code);
        Assert.Contains("inspect the resource before retrying", error.ToString());
        Assert.Empty(output.ToString());
    }

    internal static StubHandler EnvironmentAndCheckReplies() => new(
        Reply("""{"id":42,"name":"production"}"""), Reply("""{"id":99,"resource":{"id":"42","type":"environment"}}"""));

    internal static async Task<(int Code, string Output, string Error)> Run(StubHandler handler, string[] args, Func<string, string?>? settings = null)
    {
        using var http = new HttpClient(handler, disposeHandler: false);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await CliApplication.RunAsync(args, http, settings ?? Settings, output, error);
        return (code, output.ToString(), error.ToString());
    }

    internal static string? Settings(string name) => name switch
    {
        "AZURE_DEVOPS_ORGANIZATION" => "my-org",
        "AZURE_DEVOPS_PROJECT" => "my-project",
        "AZURE_DEVOPS_PAT" => Pat,
        _ => null
    };

    internal static HttpResponseMessage Reply(string json, HttpStatusCode status = HttpStatusCode.OK, string? continuation = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (continuation is not null)
            response.Headers.Add("x-ms-continuationtoken", continuation);
        return response;
    }
}

internal sealed record CapturedRequest(HttpMethod Method, string Url, string? Authorization, string Accept, string? ContentType, JsonObject? Body);

internal sealed class StubHandler(params HttpResponseMessage[] replies) : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> responses = new(replies);
    internal List<CapturedRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new CapturedRequest(request.Method, request.RequestUri!.AbsoluteUri,
            request.Headers.Authorization?.ToString(), request.Headers.Accept.ToString(),
            request.Content?.Headers.ContentType?.ToString(), json is null ? null : JsonNode.Parse(json)!.AsObject()));
        Assert.NotEmpty(responses);
        return responses.Dequeue();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            foreach (var response in responses)
                response.Dispose();
        base.Dispose(disposing);
    }
}
