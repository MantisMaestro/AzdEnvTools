**AzdEnvTools**

`azd-env` is a .NET 10 CLI for Azure DevOps Services environments and their approval/check configurations. It uses PAT authentication and REST API version `7.2-preview.1`.

Environments are **project-scoped, not organization-wide**. Every operation requires an organization and project. `approvals add` configures an approval check; it does not approve a pending deployment. Environment deletion and check update/deletion are not implemented.

**Build And Run**

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Run these commands from the repository root:

```bash
dotnet build AzdEnvTools.slnx
dotnet run --project AzdEnvTools.Cli -- --help
dotnet run --project AzdEnvTools.Cli -- environments list
```

The last command needs the authentication settings below. No Azure CLI or Azure Developer CLI login is used.

To pack version `0.1.0` and install the tool from the local package into a repository-local directory:

```bash
dotnet pack AzdEnvTools.Cli/AzdEnvTools.Cli.csproj -c Release -o ./artifacts
dotnet tool install AzdEnvTools.Cli --version 0.1.0 --add-source ./artifacts --tool-path ./.tools
export PATH="$PWD/.tools:$PATH"
azd-env --help
```

The package ID is `AzdEnvTools.Cli`; the installed command is `azd-env`. This is a tool-path installation, not a tool-manifest installation. The PATH change applies to the current shell. Without it, use `./.tools/azd-env`. The remaining examples assume `azd-env` is on PATH; you can instead replace it with `dotnet run --project AzdEnvTools.Cli --`.

**Authentication**

| Environment variable | Command-line override | Value |
| --- | --- | --- |
| `AZURE_DEVOPS_ORGANIZATION` | `--organization`, `--org` | Organization name or `https://dev.azure.com/{organization}` |
| `AZURE_DEVOPS_PROJECT` | `--project` | Project name or ID |
| `AZURE_DEVOPS_PAT` | `--pat` | Personal access token |

Explicit options override environment variables. Legacy `*.visualstudio.com` organization URLs and arbitrary server URLs are not accepted.

In Bash, read the PAT without echoing it or putting its literal value in shell history. Disable shell tracing before handling secrets:

```bash
set +x
export AZURE_DEVOPS_ORGANIZATION='my-organization'
export AZURE_DEVOPS_PROJECT='my-project'
read -rsp 'Azure DevOps PAT: ' AZURE_DEVOPS_PAT
printf '\n'
export AZURE_DEVOPS_PAT

azd-env environments list

# After completing your CLI work:
unset AZURE_DEVOPS_PAT
```

Do not type a literal token into an `export` command. `--pat` is supported but risks exposing the token through shell history, process arguments, and logs. Even `--pat "$AZURE_DEVOPS_PAT"` exposes the expanded token in process arguments. Environment variables are not a secret vault: child processes inherit them, and sufficiently privileged processes can inspect them. In CI, use a masked secret variable and avoid logging the environment.

**Scopes And Permissions**

For the full command set, provision a PAT with these documented scopes:

| Scope identifier | PAT UI label to look for | Use |
| --- | --- | --- |
| `vso.environment_manage` | Environment: Read & manage | Manage environments; environment list also documents this scope. |
| `vso.build_execute` | Build: Read & execute | Environment creation and adding checks. Includes `vso.build` read access used by list operations. |
| `vso.pipelineresources_manage` | Pipeline Resources: Use & manage | Add protected-resource checks, including approval checks. |

PAT UI names/availability can vary; the API scope identifiers and linked Microsoft references are the authoritative guide. Use least privilege for the operations you need. Scopes do not grant the token owner project access or environment administration rights: the user must also have the appropriate project/environment permissions to read, create, or manage the target resource and its checks. Adding a check first reads the environment, so environment read access is needed too. Organization PAT policies may restrict scope selection.

**Environments**

```bash
# List all environments in the selected project, following pagination.
azd-env environments list
azd-env environments list --name production

# Inspect a single environment by its positive integer ID.
azd-env environments get --id 42

azd-env environments create --name production --description 'Production deployments'

# Only supplied fields change. At least one field must be supplied.
azd-env environments update --id 42 --name production-east
azd-env environments update --id 42 --description 'East region deployments'
azd-env environments update --id 42 --description ''

# Override the configured project for one invocation.
azd-env environments list --org my-organization --project 'Another Project'
```

Replace `42` with an ID returned by list/create in the target project. Omitted update fields remain unchanged; an empty description clears it. Names must not be empty or whitespace.

**Approvals**

Approvers must be **Azure DevOps user/group identity GUIDs**, not email addresses, Microsoft Entra object IDs, or Graph descriptors. The CLI does not look up or automatically resolve identities. Replace the placeholder GUIDs below before running:

```bash
azd-env approvals add --environment-id 42 \
  --approver 11111111-1111-4111-8111-111111111111 \
  --approver 22222222-2222-4222-8222-222222222222 \
  --instructions 'Review the deployment before approving.'
```

| Option | Behavior/default |
| --- | --- |
| `--environment-id` | Required positive integer environment ID. |
| `--approver` | Required; repeat the option or supply multiple GUIDs after it. IDs must be non-empty and unique. |
| `--minimum-approvers` | Defaults to the number of specified approvers: **all are required**. Explicit values must be from 1 to that number. |
| `--execution-order` | `anyOrder` (default) or `inSequence`. |
| `--instructions` | Optional approver instructions; defaults to an empty string. |
| `--allow-self-approval` | Opt-in. By default the requester is blocked from approving (`requesterCannotBeApprover: true`). |
| `--timeout` | Minutes, from 1 to 43200; defaults to 43200 (30 days). |

For manual identity lookup, see Microsoft's [Read Identities API](https://learn.microsoft.com/en-us/rest/api/azure/devops/ims/identities/read-identities?view=azure-devops-rest-7.1). It uses `GET https://vssps.dev.azure.com/{organization}/_apis/identities?api-version=7.1`, with documented `searchFilter`/`filterValue` parameters, and requires the additional `vso.identity` (Identity: Read) scope. Inspect the matches and use the intended identity's `id` (storage key/VSID), not its descriptor or an Entra object identifier in its properties. Identity lookup is separate from this CLI and is not required if you already know the correct IDs.

**Generic Checks**

List check configurations, including type-specific settings:

```bash
azd-env checks list --environment-id 42
```

Add one check from a JSON object in a file or from stdin. These are alternative ways to perform the same write, not steps to run together:

```bash
azd-env checks add --environment-id 42 --file examples/approval.json
```

```bash
azd-env checks add --environment-id 42 --file - < examples/approval.json
```

[examples/approval.json](examples/approval.json) is a small approval-check example with one required approver and requester self-approval blocked. **Replace its placeholder approver GUID with a real Azure DevOps identity GUID before use.** The approval check type GUID is fixed and must not be replaced with an identity GUID.

The generic command requires:

- `type`: an object containing `id`, a non-empty check type GUID.
- `settings`: an object with the settings required by that specific check type.
- `timeout`: a positive integer number of minutes. The server may impose type-specific limits; the generic command does not enforce the approval helper's 43200-minute maximum.

There is no universal `settings` schema. The CLI validates only the outer shape and forwards the supplied `type` and `settings` objects; Azure DevOps validates their type-specific contents. Unlike `approvals add`, `checks add` does not fill in approval defaults. Consult Microsoft's [Check Configurations - Add examples](https://learn.microsoft.com/en-us/rest/api/azure/devops/approvalsandchecks/check-configurations/add?view=azure-devops-rest-7.2#examples), which include Approval and Business Hours (Task Check) payloads. Those examples use a queue resource; this CLI always binds checks to an environment.

For another check type, configure a representative check in the Azure DevOps UI, then retrieve it using `checks list`. Select **one object** from the returned array, review its settings and any source-specific IDs, and use it as your input. With optional `jq`, for example:

```bash
# Replace 123 with the desired check configuration ID from checks list.
azd-env checks list --environment-id 42 | jq -e '.[] | select(.id == 123)'
```

Do not pass the whole list array to `checks add`. Only the input's top-level `type`, `settings`, and `timeout` are copied into the create request. The CLI fetches the environment selected by `--environment-id` and supplies `resource.type`, `resource.id`, and `resource.name`, **overriding any JSON `resource`**. Other top-level fields, including server metadata (`id`, `version`, timestamps, identities, URLs, and links) and `isDisabled`, are discarded. Reusing a listed object creates a new check; it does not update the original or preserve its disabled state. Review exported check settings before sharing or committing them because they may contain sensitive values.

**Output And Failures**

- Successful data commands write indented JSON to stdout. Lists return arrays, not the REST API's `{ "count": ..., "value": [...] }` wrapper; get/create/update/add return single objects.
- Both list commands follow server continuation tokens and aggregate results. Check listing requests expanded settings.
- Success exits with code `0`. Invalid input and request failures exit nonzero, with diagnostics on stderr.
- Requests have a 100-second HTTP timeout. Writes are **not retried automatically**, including after rate limiting or server errors.
- A timeout, cancellation, or connection failure does not prove a write failed. Inspect the environment with list/get, or its checks with `checks list`, before retrying; a repeated create/add may duplicate a completed operation.
- For HTTP 401, check PAT validity/expiry. For 403, check both scopes and resource permissions. For 404, check organization, project, and ID. For 429, wait before retrying, applying the write caution above.

**Tests**

The test entry point is:

```bash
dotnet test AzdEnvTools.slnx
```

The xUnit tests use a mocked HTTP handler and require no PAT or Azure DevOps access. They cover request routes and payloads, authentication, pagination, partial updates, approvals, JSON checks, validation, and failure handling. No live Azure DevOps integration tests have been performed. Validate preview-API behavior and type-specific check settings in a non-production project before relying on them.

**API References**

The CLI uses Microsoft's `7.2-preview.1` environment and check configuration APIs:

- [Environments - List](https://learn.microsoft.com/en-us/rest/api/azure/devops/environments/environments/list?view=azure-devops-rest-7.2)
- [Environments - Get](https://learn.microsoft.com/en-us/rest/api/azure/devops/environments/environments/get?view=azure-devops-rest-7.2)
- [Environments - Add](https://learn.microsoft.com/en-us/rest/api/azure/devops/environments/environments/add?view=azure-devops-rest-7.2)
- [Environments - Update](https://learn.microsoft.com/en-us/rest/api/azure/devops/environments/environments/update?view=azure-devops-rest-7.2)
- [Check Configurations - List](https://learn.microsoft.com/en-us/rest/api/azure/devops/approvalsandchecks/check-configurations/list?view=azure-devops-rest-7.2)
- [Check Configurations - Add](https://learn.microsoft.com/en-us/rest/api/azure/devops/approvalsandchecks/check-configurations/add?view=azure-devops-rest-7.2)
