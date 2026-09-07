using System.CommandLine;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AzdEnvTools.Cli;

internal static class CliApplication
{
    internal static async Task<int> RunAsync(string[] args, HttpClient http, Func<string, string?> environment,
        TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        var organization = new Option<string>("--organization", "--org")
        {
            Description = "Organization name or https://dev.azure.com/{organization}; defaults to AZURE_DEVOPS_ORGANIZATION.",
            Recursive = true
        };
        var project = new Option<string>("--project")
        {
            Description = "Project name or ID; defaults to AZURE_DEVOPS_PROJECT.", Recursive = true
        };
        var pat = new Option<string>("--pat")
        {
            Description = "Personal access token; prefer AZURE_DEVOPS_PAT to avoid shell history/process-list exposure.", Recursive = true
        };
        var root = new RootCommand("Manage Azure DevOps environments, approvals, and checks. Results are JSON.")
        {
            organization, project, pat
        };

        var environments = new Command("environments", "List, inspect, create, or update environments in a project.");
        root.Subcommands.Add(environments);
        var nameFilter = new Option<string>("--name") { Description = "Filter environments by name." };
        var list = new Command("list", "List all matching environments, following pagination.") { nameFilter };
        environments.Subcommands.Add(list);
        Bind(list, async (p, client, ct) => await client.ListEnvironmentsAsync(p.GetValue(nameFilter), ct));

        var getId = IdOption("--id");
        var get = new Command("get", "Get an environment by ID.") { getId };
        environments.Subcommands.Add(get);
        Bind(get, async (p, client, ct) => await client.GetEnvironmentAsync(p.GetValue(getId), ct));

        var createName = new Option<string>("--name") { Description = "Environment name.", Required = true };
        var createDescription = new Option<string>("--description") { Description = "Environment description." };
        var create = new Command("create", "Create a new environment.") { createName, createDescription };
        environments.Subcommands.Add(create);
        Bind(create, async (p, client, ct) => await client.CreateEnvironmentAsync(p.GetValue(createName)!, p.GetValue(createDescription), ct));

        var updateId = IdOption("--id");
        var updateName = new Option<string>("--name") { Description = "New name; omitted means unchanged." };
        var updateDescription = new Option<string>("--description") { Description = "New description; pass an empty string to clear it." };
        var update = new Command("update", "Update only the supplied environment fields.") { updateId, updateName, updateDescription };
        environments.Subcommands.Add(update);
        Bind(update, async (p, client, ct) => await client.UpdateEnvironmentAsync(p.GetValue(updateId), p.GetValue(updateName), p.GetValue(updateDescription), ct));

        var checks = new Command("checks", "Inspect or add environment check configurations.");
        root.Subcommands.Add(checks);
        var listEnvironmentId = IdOption("--environment-id");
        var listChecks = new Command("list", "List checks, including their type-specific settings.") { listEnvironmentId };
        checks.Subcommands.Add(listChecks);
        Bind(listChecks, async (p, client, ct) => await client.ListChecksAsync(p.GetValue(listEnvironmentId), ct));

        var checkEnvironmentId = IdOption("--environment-id");
        var file = new Option<string>("--file") { Description = "JSON configuration with type.id, settings, and timeout. Use '-' for stdin.", Required = true };
        var addCheck = new Command("add", "Add a check from JSON, binding it to the selected environment.") { checkEnvironmentId, file };
        checks.Subcommands.Add(addCheck);
        Bind(addCheck, async (p, client, ct) =>
        {
            var path = p.GetValue(file)!;
            var json = path == "-" ? await Console.In.ReadToEndAsync(ct) : await File.ReadAllTextAsync(path, ct);
            var configuration = JsonNode.Parse(json) as JsonObject
                ?? throw new ArgumentException("Check JSON must be an object.");
            return await client.AddCheckAsync(p.GetValue(checkEnvironmentId), configuration, ct);
        });

        var approvals = new Command("approvals", "Configure approval checks (not approve pending deployments).");
        root.Subcommands.Add(approvals);
        var approvalEnvironmentId = IdOption("--environment-id");
        var approvers = new Option<Guid[]>("--approver")
        {
            Description = "Azure DevOps user/group identity GUID. Repeat for multiple approvers (not email or Entra object ID).",
            Required = true, AllowMultipleArgumentsPerToken = true, Arity = ArgumentArity.OneOrMore,
            CustomParser = result =>
            {
                var ids = new List<Guid>();
                foreach (var argument in result.Tokens)
                {
                    if (!Guid.TryParse(argument.Value, out var id))
                    {
                        result.AddError("--approver must contain Azure DevOps identity GUIDs, not email addresses or descriptors.");
                        return [];
                    }
                    ids.Add(id);
                }
                return ids.ToArray();
            }
        };
        var minimum = new Option<int?>("--minimum-approvers") { Description = "Required approval count; defaults to all specified approvers." };
        var order = new Option<string>("--execution-order")
        {
            Description = "Approval execution order: anyOrder or inSequence.", DefaultValueFactory = _ => "anyOrder"
        };
        order.AcceptOnlyFromAmong("anyOrder", "inSequence");
        var instructions = new Option<string>("--instructions") { Description = "Instructions shown to approvers." };
        var allowSelfApproval = new Option<bool>("--allow-self-approval") { Description = "Allow the requester to approve; forbidden by default." };
        var timeout = new Option<int>("--timeout") { Description = "Approval timeout in minutes (1-43200).", DefaultValueFactory = _ => 43200 };
        var addApproval = new Command("add", "Add an approval check to an environment.")
        {
            approvalEnvironmentId, approvers, minimum, order, instructions, allowSelfApproval, timeout
        };
        approvals.Subcommands.Add(addApproval);
        Bind(addApproval, async (p, client, ct) =>
        {
            var ids = p.GetValue(approvers)!;
            var configuration = ApprovalConfiguration.Create(ids, p.GetValue(minimum) ?? ids.Length,
                p.GetValue(order)!, p.GetValue(instructions), p.GetValue(allowSelfApproval), p.GetValue(timeout));
            return await client.AddCheckAsync(p.GetValue(approvalEnvironmentId), configuration, ct);
        });

        string? token = null;
        try
        {
            var parsed = root.Parse(args);
            token = parsed.GetValue(pat) ?? environment("AZURE_DEVOPS_PAT");
            return await parsed.InvokeAsync(new InvocationConfiguration
            {
                Output = output, Error = error, EnableDefaultExceptionHandler = false
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("Error: Request cancelled or timed out. A write may have completed; inspect the resource before retrying.");
            return 1;
        }
        catch (Exception ex) when (ex is ArgumentException or HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            var message = ex is JsonException ? "Invalid JSON in check configuration." : ex.Message;
            if (!string.IsNullOrEmpty(token))
            {
                message = message.Replace(Convert.ToBase64String(Encoding.UTF8.GetBytes($":{token}")), "[REDACTED]", StringComparison.Ordinal)
                    .Replace(token, "[REDACTED]", StringComparison.Ordinal);
            }
            await error.WriteLineAsync($"Error: {message}");
            return 1;
        }

        void Bind(Command command, Func<ParseResult, AzureDevOpsClient, CancellationToken, Task<JsonNode>> action)
        {
            command.SetAction(async (p, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                var client = new AzureDevOpsClient(http,
                    p.GetValue(organization) ?? environment("AZURE_DEVOPS_ORGANIZATION"),
                    p.GetValue(project) ?? environment("AZURE_DEVOPS_PROJECT"),
                    p.GetValue(pat) ?? environment("AZURE_DEVOPS_PAT"));
                var result = await action(p, client, ct);
                await output.WriteLineAsync(result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            });
        }
    }

    private static Option<int> IdOption(string name)
    {
        return new Option<int>(name)
        {
            Description = "Positive environment ID.", Required = true,
            CustomParser = result =>
            {
                if (result.Tokens.Count != 1 || !int.TryParse(result.Tokens[0].Value, out var id) || id <= 0)
                {
                    result.AddError($"{name} must be a positive integer.");
                    return 0;
                }
                return id;
            }
        };
    }
}
