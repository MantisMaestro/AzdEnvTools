using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AzdEnvTools.Cli;

internal sealed class AzureDevOpsClient
{
    internal const string ApiVersion = "7.2-preview.1";
    private readonly HttpClient http;
    private readonly string baseUrl;
    private readonly string authorization;

    internal AzureDevOpsClient(HttpClient http, string? organization, string? project, string? pat)
    {
        if (string.IsNullOrWhiteSpace(organization))
            throw new ArgumentException("Specify --organization or AZURE_DEVOPS_ORGANIZATION.");
        if (string.IsNullOrWhiteSpace(project))
            throw new ArgumentException("Specify --project or AZURE_DEVOPS_PROJECT.");
        if (string.IsNullOrWhiteSpace(pat))
            throw new ArgumentException("Set AZURE_DEVOPS_PAT (recommended) or specify --pat.");

        organization = organization.Trim().TrimEnd('/');
        if (Uri.TryCreate(organization, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme != "https" || uri.Host != "dev.azure.com" || !uri.IsDefaultPort ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                uri.AbsolutePath.Trim('/').Contains('/'))
                throw new ArgumentException("Organization URL must be https://dev.azure.com/{organization}.");
            organization = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
        }

        if (organization.Length == 0 || organization is "." or ".." ||
            organization.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            throw new ArgumentException("Specify an organization name or https://dev.azure.com/{organization}.");
        if (project is "." or "..")
            throw new ArgumentException("Project must be a project name or ID.");

        this.http = http;
        baseUrl = $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(project)}/_apis/pipelines/";
        authorization = Convert.ToBase64String(Encoding.UTF8.GetBytes($":{pat}"));
    }

    internal Task<JsonArray> ListEnvironmentsAsync(string? name, CancellationToken cancellationToken) =>
        ListAsync("environments", name is null ? "" : $"&name={Uri.EscapeDataString(name)}", cancellationToken);

    internal async Task<JsonObject> GetEnvironmentAsync(int id, CancellationToken cancellationToken) =>
        (await SendAsync(HttpMethod.Get, $"environments/{PositiveId(id)}", null, cancellationToken)).Body;

    internal async Task<JsonObject> CreateEnvironmentAsync(string name, string? description, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("--name must not be empty.");
        var body = new JsonObject { ["name"] = name };
        if (description is not null)
            body["description"] = description;
        return (await SendAsync(HttpMethod.Post, "environments", body, cancellationToken)).Body;
    }

    internal async Task<JsonObject> UpdateEnvironmentAsync(int id, string? name, string? description, CancellationToken cancellationToken)
    {
        PositiveId(id);
        if (name is null && description is null)
            throw new ArgumentException("Supply --name and/or --description for an update.");
        if (name is not null && string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("--name must not be empty.");

        // Omit unspecified fields so a description-only update does not rename the environment.
        var body = new JsonObject();
        if (name is not null)
            body["name"] = name;
        if (description is not null)
            body["description"] = description;
        return (await SendAsync(HttpMethod.Patch, $"environments/{id}", body, cancellationToken)).Body;
    }

    internal Task<JsonArray> ListChecksAsync(int environmentId, CancellationToken cancellationToken) =>
        ListAsync("checks/configurations", $"&resourceType=environment&resourceId={PositiveId(environmentId)}&$expand=settings", cancellationToken);

    internal async Task<JsonObject> AddCheckAsync(int environmentId, JsonObject configuration, CancellationToken cancellationToken)
    {
        PositiveId(environmentId);
        if (configuration["type"] is not JsonObject type ||
            type["id"] is not JsonValue typeId || !typeId.TryGetValue<string>(out var typeIdText) ||
            !Guid.TryParse(typeIdText, out var guid) || guid == Guid.Empty)
            throw new ArgumentException("Check JSON must contain type.id as a non-empty check type GUID.");
        if (configuration["settings"] is not JsonObject)
            throw new ArgumentException("Check JSON must contain a settings object appropriate for the check type.");
        if (configuration["timeout"] is not JsonValue timeout || !timeout.TryGetValue<int>(out var minutes) || minutes < 1)
            throw new ArgumentException("Check JSON must contain a positive integer timeout in minutes.");

        var environment = await GetEnvironmentAsync(environmentId, cancellationToken);
        // Only send writable configuration fields; always bind to the explicitly selected environment.
        var body = new JsonObject
        {
            ["type"] = type.DeepClone(),
            ["settings"] = configuration["settings"]!.DeepClone(),
            ["timeout"] = minutes,
            ["resource"] = new JsonObject
            {
                ["type"] = "environment",
                ["id"] = environmentId.ToString(CultureInfo.InvariantCulture),
                ["name"] = environment["name"]?.DeepClone()
            }
        };
        return (await SendAsync(HttpMethod.Post, "checks/configurations", body, cancellationToken)).Body;
    }

    private async Task<JsonArray> ListAsync(string path, string query, CancellationToken cancellationToken)
    {
        var items = new JsonArray();
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        string? continuation = null;
        do
        {
            var pageQuery = query + (continuation is null ? "" : $"&continuationToken={Uri.EscapeDataString(continuation)}");
            var (body, next) = await SendAsync(HttpMethod.Get, path, null, cancellationToken, pageQuery);
            if (body["value"] is not JsonArray values)
                throw new InvalidDataException("Azure DevOps returned a list response without a value array.");
            foreach (var value in values)
                items.Add(value?.DeepClone());
            continuation = next;
            if (continuation is not null && !seenTokens.Add(continuation))
                throw new InvalidDataException("Azure DevOps returned a repeated continuation token; refusing an incomplete list.");
        } while (continuation is not null);
        return items;
    }

    private async Task<(JsonObject Body, string? Continuation)> SendAsync(
        HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken, string query = "")
    {
        using var request = new HttpRequestMessage(method, $"{baseUrl}{path}?api-version={ApiVersion}{query}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authorization);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string? message = null;
            try
            {
                if (JsonNode.Parse(text) is JsonObject error && error["message"] is JsonValue value &&
                    value.TryGetValue<string>(out var serverMessage))
                    message = serverMessage;
            }
            catch (JsonException) { }

            var hint = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Check that your PAT is valid and has not expired.",
                HttpStatusCode.Forbidden => "Check PAT scopes and project/environment permissions.",
                HttpStatusCode.NotFound => "Check the organization, project, and resource ID.",
                HttpStatusCode.TooManyRequests => "Azure DevOps is rate limiting requests; retry later.",
                _ when (int)response.StatusCode is >= 300 and < 400 => "Redirect refused. Check the organization URL and PAT.",
                _ => "The operation was not retried automatically."
            };
            throw new HttpRequestException($"Azure DevOps returned HTTP {(int)response.StatusCode}. {hint}{(message is null ? "" : $" {message}")}", null, response.StatusCode);
        }

        JsonObject result;
        try
        {
            result = JsonNode.Parse(text) as JsonObject
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Azure DevOps returned an unexpected response instead of a JSON object. Check the organization URL and authentication.");
        }
        var continuation = response.Headers.TryGetValues("x-ms-continuationtoken", out var tokens)
            ? tokens.FirstOrDefault() : null;
        return (result, string.IsNullOrWhiteSpace(continuation) ? null : continuation);
    }

    private static int PositiveId(int id) => id > 0 ? id : throw new ArgumentException("Environment ID must be a positive integer.");
}
