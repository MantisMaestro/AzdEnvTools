using System.Text.Json.Nodes;

namespace AzdEnvTools.Cli;

internal static class ApprovalConfiguration
{
    internal const string TypeId = "8c6f20a7-a545-4486-9777-f762fafe0d4d";

    internal static JsonObject Create(Guid[] approvers, int minimum, string executionOrder,
        string? instructions, bool allowSelfApproval, int timeout)
    {
        if (approvers.Length == 0 || approvers.Any(id => id == Guid.Empty))
            throw new ArgumentException("Supply at least one non-empty Azure DevOps identity GUID with --approver.");
        if (approvers.Distinct().Count() != approvers.Length)
            throw new ArgumentException("Approver IDs must be unique.");
        if (minimum < 1 || minimum > approvers.Length)
            throw new ArgumentException("--minimum-approvers must be between 1 and the number of approvers.");
        if (executionOrder is not ("anyOrder" or "inSequence"))
            throw new ArgumentException("--execution-order must be anyOrder or inSequence.");
        if (timeout is < 1 or > 43200)
            throw new ArgumentException("Approval --timeout must be between 1 and 43200 minutes.");

        return new JsonObject
        {
            ["type"] = new JsonObject { ["id"] = TypeId, ["name"] = "Approval" },
            ["timeout"] = timeout,
            ["settings"] = new JsonObject
            {
                ["approvers"] = new JsonArray(approvers.Select(id => (JsonNode)new JsonObject { ["id"] = id.ToString() }).ToArray()),
                ["minRequiredApprovers"] = minimum,
                ["executionOrder"] = executionOrder,
                ["instructions"] = instructions ?? "",
                ["requesterCannotBeApprover"] = !allowSelfApproval,
                ["blockedApprovers"] = new JsonArray()
            }
        };
    }
}
