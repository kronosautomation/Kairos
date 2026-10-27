namespace Kairos.Core.Tests;

/// <summary>Layer 7, authorization matrix: every role x every operation x own/other namespace, generated from one expected table.</summary>
public sealed class AccessPolicyTests
{
    private static readonly TenantScope Own = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a1"));

    /// <summary>The expected matrix, written out independently of the implementation. 'x' = allowed.</summary>
    private static readonly Dictionary<Operation, string> Expected = new()
    {
        //                                reader worker operator admin approver agentworker
        [Operation.StartRun] = "  xx  ",
        [Operation.SignalRun] = "  xx  ",
        [Operation.CancelRun] = "  xx  ",
        [Operation.TerminateRun] = "  xx  ",
        [Operation.ReadRun] = "x xxx ",
        [Operation.ReadAuditLog] = "   x  ",
        [Operation.PollWorkflowTask] = " x   x",
        [Operation.PollActivityTask] = " x   x",
        [Operation.CompleteTask] = " x   x",
        [Operation.HeartbeatTask] = " x   x",
        [Operation.ResumeStuckTask] = "   x  ",
        [Operation.FailRun] = "   x  ",
        [Operation.DecideApproval] = "    x ",
        [Operation.ExportHistory] = "  xx  ",
        [Operation.ManagePolicy] = "   x  ",
        [Operation.ReadPolicy] = "  xx  ",
        [Operation.EvaluatePolicy] = " x   x",
        [Operation.StartConversation] = "     x",
        [Operation.SignalConversation] = "     x",
        [Operation.CancelConversation] = "     x",
        [Operation.ReadConversation] = "     x",
    };

    private static readonly Role[] Roles = [Role.Reader, Role.Worker, Role.Operator, Role.Admin, Role.Approver, Role.AgentWorker];

    public static TheoryData<Operation> AllOperations()
    {
        var data = new TheoryData<Operation>();
        foreach (Operation op in Enum.GetValues<Operation>())
        {
            data.Add(op);
        }

        return data;
    }

    [Fact]
    public void Every_operation_is_classified_in_the_expected_matrix()
    {
        foreach (Operation op in Enum.GetValues<Operation>())
        {
            Assert.True(Expected.ContainsKey(op), $"{op} is not classified in the expected matrix.");
        }
    }

    [Theory]
    [MemberData(nameof(AllOperations))]
    public void Each_role_may_do_exactly_what_the_matrix_says_in_its_own_namespace_and_nothing_elsewhere(Operation operation)
    {
        string row = Expected[operation];
        TenantScope[] elsewhere =
        [
            new(Own.TenantId, Guid.NewGuid()),          // same tenant, another namespace
            new(Guid.NewGuid(), Own.NamespaceId),       // another tenant, same namespace id
            new(Guid.NewGuid(), Guid.NewGuid()),
        ];

        for (int i = 0; i < Roles.Length; i++)
        {
            var principal = new Principal(Own, Roles[i], "someone", null);
            Assert.Equal(row[i] == 'x', AccessPolicy.IsAllowed(principal, operation, Own));
            foreach (TenantScope other in elsewhere)
            {
                Assert.False(AccessPolicy.IsAllowed(principal, operation, other), $"{Roles[i]} must not {operation} outside its own namespace.");
            }
        }
    }

    [Fact]
    public void An_agent_worker_holds_what_a_worker_holds_and_the_conversation_operations_and_nothing_a_person_does()
    {
        var plain = new Principal(Own, Role.Worker, "w", null);
        var agent = new Principal(Own, Role.AgentWorker, "aw", null);

        Assert.All(AccessPolicy.For(Role.Worker), op => Assert.True(AccessPolicy.IsAllowed(agent, op, Own), $"{op}"));
        Assert.All(new[] { Operation.StartConversation, Operation.SignalConversation, Operation.CancelConversation, Operation.ReadConversation }, op =>
        {
            Assert.True(AccessPolicy.IsAllowed(agent, op, Own));
            Assert.False(AccessPolicy.IsAllowed(plain, op, Own)); // an ordinary worker does not get them with an upgrade
        });
        Assert.All(
            new[] { Operation.StartRun, Operation.SignalRun, Operation.CancelRun, Operation.TerminateRun, Operation.ReadRun, Operation.DecideApproval, Operation.ReadAuditLog, Operation.ExportHistory, Operation.ManagePolicy, Operation.ReadPolicy, Operation.FailRun, Operation.ResumeStuckTask },
            op => Assert.False(AccessPolicy.IsAllowed(agent, op, Own), $"{op}"));
        Assert.All(new[] { Role.Reader, Role.Operator, Role.Admin, Role.Approver }, role =>
            Assert.All(new[] { Operation.StartConversation, Operation.SignalConversation, Operation.CancelConversation, Operation.ReadConversation }, op => Assert.False(AccessPolicy.IsAllowed(new Principal(Own, role, "p", null), op, Own))));
    }

    [Fact]
    public void A_worker_credential_cannot_start_or_read_workflows_and_a_reader_cannot_write()
    {
        var worker = new Principal(Own, Role.Worker, "w", null);
        var reader = new Principal(Own, Role.Reader, "r", null);

        Assert.False(AccessPolicy.IsAllowed(worker, Operation.StartRun, Own));
        Assert.False(AccessPolicy.IsAllowed(worker, Operation.ReadRun, Own));
        foreach (Operation op in Enum.GetValues<Operation>().Where(o => o != Operation.ReadRun))
        {
            Assert.False(AccessPolicy.IsAllowed(reader, op, Own));
        }
    }
}
