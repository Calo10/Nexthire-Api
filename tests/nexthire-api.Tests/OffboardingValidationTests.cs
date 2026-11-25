using FluentAssertions;
using nexthire_api.DTOs.CoreHr;
using nexthire_api.Services;
using Xunit;

namespace nexthire_api.Tests;

public class OffboardingValidationTests
{
    private sealed class TestTask : IOffboardingTaskState
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Title { get; set; } = "Task";
        public string Status { get; set; } = OffboardingTaskStatuses.Pending;
        public bool IsWaived { get; set; }
        public bool IsRequired { get; set; } = true;
        public bool RequiresApproval { get; set; }
        public string? ApprovalStatus { get; set; }
        public DateTime? DueDate { get; set; }
        public Guid? DependsOnTaskId { get; set; }
    }

    private sealed class TestAsset : IOffboardingAssetState
    {
        public string AssetName { get; set; } = "Laptop";
        public bool ReturnRequired { get; set; } = true;
        public string Status { get; set; } = OffboardingAssetStatuses.PendingReturn;
    }

    private sealed class TestAccess : IOffboardingAccessState
    {
        public string SystemName { get; set; } = "Email";
        public string Status { get; set; } = OffboardingAccessStatuses.Pending;
    }

    private sealed class TestSettlement : IOffboardingSettlementState
    {
        public string Title { get; set; } = "Final pay";
        public bool IsRequired { get; set; } = true;
        public string Status { get; set; } = OffboardingSettlementStatuses.Pending;
    }

    private static TestTask Done(string title = "Done", bool required = true) =>
        new() { Title = title, Status = OffboardingTaskStatuses.Completed, IsRequired = required };

    private static TestTask Open(string title = "Open", bool required = true) =>
        new() { Title = title, Status = OffboardingTaskStatuses.Pending, IsRequired = required };

    private static TestAsset Asset(string status, bool required = true, string name = "Laptop") =>
        new() { AssetName = name, Status = status, ReturnRequired = required };

    private static TestAccess Access(string status, string name = "Email") => new() { SystemName = name, Status = status };

    private static TestSettlement Settlement(string status, bool required = true, string title = "Final pay") =>
        new() { Title = title, Status = status, IsRequired = required };

    private static readonly IOffboardingAssetState[] ClearedAssets = [Asset(OffboardingAssetStatuses.Returned)];
    private static readonly IOffboardingAccessState[] ClearedAccess = [Access(OffboardingAccessStatuses.Revoked)];
    private static readonly IOffboardingSettlementState[] ClearedSettlement = [Settlement(OffboardingSettlementStatuses.Completed)];

    // -------------------------------------------------------------------------
    // Status transitions
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("Scheduled", "InProgress", true)]
    [InlineData("InProgress", "PendingClearance", true)]
    [InlineData("PendingClearance", "Completed", true)]
    [InlineData("PendingClearance", "InProgress", true)]
    [InlineData("InProgress", "Completed", true)]   // Kanban fast path (same validation)
    [InlineData("Scheduled", "Cancelled", true)]
    [InlineData("InProgress", "Cancelled", true)]
    [InlineData("PendingClearance", "Cancelled", true)]
    [InlineData("Scheduled", "PendingClearance", false)]
    [InlineData("Scheduled", "Completed", false)]
    [InlineData("InProgress", "Scheduled", false)]
    [InlineData("PendingClearance", "Scheduled", false)]
    [InlineData("Completed", "InProgress", false)]
    [InlineData("Completed", "Cancelled", false)]
    [InlineData("Cancelled", "InProgress", false)]
    [InlineData("Cancelled", "Scheduled", false)]
    [InlineData("InProgress", "InProgress", false)]
    [InlineData("InProgress", "Bogus", false)]
    [InlineData(null, "InProgress", false)]
    public void CanTransition_follows_the_status_machine(string? from, string? to, bool expected)
    {
        OffboardingValidation.CanTransition(from, to).Should().Be(expected);
    }

    [Theory]
    [InlineData("Scheduled", true)]
    [InlineData("InProgress", true)]
    [InlineData("PendingClearance", true)]
    [InlineData("Completed", false)]
    [InlineData("Cancelled", false)]
    [InlineData(null, false)]
    public void IsActiveStatus_covers_the_three_open_statuses(string? status, bool expected)
    {
        OffboardingValidation.IsActiveStatus(status).Should().Be(expected);
        OffboardingValidation.IsTerminalStatus(status).Should().Be(status is "Completed" or "Cancelled");
    }

    [Fact]
    public void Starting_does_not_depend_on_task_state()
    {
        OffboardingValidation.ValidateTransition("Scheduled", "InProgress", [Open()]).Should().BeNull();
    }

    [Fact]
    public void Cannot_skip_straight_from_Scheduled_to_PendingClearance_or_Completed()
    {
        OffboardingValidation.ValidateTransition("Scheduled", "PendingClearance", [Done()]).Should().NotBeNull();
        OffboardingValidation.ValidateTransition("Scheduled", "Completed", [Done()], null, ClearedAssets, ClearedAccess, ClearedSettlement)
            .Should().Contain("cannot move from Scheduled to Completed");
    }

    [Fact]
    public void Same_status_and_terminal_statuses_are_rejected_with_clear_messages()
    {
        OffboardingValidation.ValidateTransition("InProgress", "InProgress", [Done()]).Should().Contain("already");
        OffboardingValidation.ValidateTransition("Completed", "InProgress", [Done()]).Should().Contain("cannot change status");
        OffboardingValidation.ValidateTransition("Cancelled", "Scheduled", [Done()]).Should().Contain("cannot change status");
        OffboardingValidation.ValidateTransition("InProgress", "Nope", [Done()]).Should().Contain("Status must be one of");
    }

    [Fact]
    public void Cancelling_requires_a_reason_but_no_task_gate()
    {
        OffboardingValidation.ValidateTransition("InProgress", "Cancelled", [Open()], null).Should().Contain("reason");
        OffboardingValidation.ValidateTransition("InProgress", "Cancelled", [Open()], "   ").Should().Contain("reason");
        OffboardingValidation.ValidateTransition("InProgress", "Cancelled", [Open()], "Employee withdrew resignation").Should().BeNull();
        OffboardingValidation.ValidateTransition("Scheduled", "Cancelled", [Open()], "Entered by mistake").Should().BeNull();
    }

    [Fact]
    public void PendingClearance_requires_every_required_task_to_be_complete()
    {
        OffboardingValidation.ValidateTransition("InProgress", "PendingClearance", [Done("A"), Open("B Handover")])
            .Should().Contain("B Handover");

        OffboardingValidation.ValidateTransition("InProgress", "PendingClearance", [Done("A"), Done("B")]).Should().BeNull();
    }

    [Fact]
    public void PendingClearance_ignores_optional_waived_and_cancelled_tasks()
    {
        var optionalOpen = Open("Optional", required: false);
        var waived = new TestTask { Title = "Waived", IsWaived = true };
        var cancelled = new TestTask { Title = "Cancelled", Status = OffboardingTaskStatuses.Cancelled };

        OffboardingValidation.ValidateTransition("InProgress", "PendingClearance", [Done(), optionalOpen, waived, cancelled])
            .Should().BeNull();
    }

    [Fact]
    public void Approval_gated_tasks_need_approval_before_clearance()
    {
        var awaiting = new TestTask
        {
            Title = "Manager sign-off", Status = OffboardingTaskStatuses.Completed,
            RequiresApproval = true, ApprovalStatus = OffboardingApprovalStatuses.Pending
        };
        var rejected = new TestTask
        {
            Title = "Legal sign-off", Status = OffboardingTaskStatuses.Completed,
            RequiresApproval = true, ApprovalStatus = OffboardingApprovalStatuses.Rejected
        };
        var approved = new TestTask
        {
            Title = "Finance sign-off", Status = OffboardingTaskStatuses.Completed,
            RequiresApproval = true, ApprovalStatus = OffboardingApprovalStatuses.Approved
        };

        OffboardingValidation.ValidateTransition("InProgress", "PendingClearance", [awaiting]).Should().Contain("Manager sign-off");
        OffboardingValidation.ValidateTransition("InProgress", "PendingClearance", [rejected]).Should().Contain("Legal sign-off");
        OffboardingValidation.ValidateTransition("InProgress", "PendingClearance", [approved]).Should().BeNull();
    }

    [Fact]
    public void Blocker_message_lists_at_most_five_titles()
    {
        var tasks = Enumerable.Range(1, 8).Select(i => Open($"T{i}")).ToArray();
        var message = OffboardingValidation.ValidateTransition("InProgress", "PendingClearance", tasks)!;

        message.Should().Contain("T1").And.Contain("T5").And.NotContain("T6").And.Contain("3 more");
    }

    // -------------------------------------------------------------------------
    // Completion gates (assets / access / settlement) and drag-to-Completed parity
    // -------------------------------------------------------------------------

    [Fact]
    public void Completed_requires_tasks_assets_access_and_settlement()
    {
        OffboardingValidation.ValidateTransition("PendingClearance", "Completed", [Done()], null, ClearedAssets, ClearedAccess, ClearedSettlement)
            .Should().BeNull();

        OffboardingValidation.ValidateTransition("PendingClearance", "Completed", [Open("Handover")], null, ClearedAssets, ClearedAccess, ClearedSettlement)
            .Should().Contain("Handover");

        OffboardingValidation.ValidateTransition("PendingClearance", "Completed", [Done()], null,
                [Asset(OffboardingAssetStatuses.PendingReturn, name: "MacBook")], ClearedAccess, ClearedSettlement)
            .Should().Contain("MacBook");

        OffboardingValidation.ValidateTransition("PendingClearance", "Completed", [Done()], null,
                ClearedAssets, [Access(OffboardingAccessStatuses.Pending, "GitHub")], ClearedSettlement)
            .Should().Contain("GitHub");

        OffboardingValidation.ValidateTransition("PendingClearance", "Completed", [Done()], null,
                ClearedAssets, ClearedAccess, [Settlement(OffboardingSettlementStatuses.Pending, title: "Final pay")])
            .Should().Contain("Final pay");
    }

    [Fact]
    public void Multiple_failing_gates_are_all_reported()
    {
        var message = OffboardingValidation.ValidateTransition("PendingClearance", "Completed",
            [Open("Handover")], null,
            [Asset(OffboardingAssetStatuses.PendingReturn, name: "MacBook")],
            [Access(OffboardingAccessStatuses.Pending, "GitHub")],
            [Settlement(OffboardingSettlementStatuses.Pending, title: "Final pay")])!;

        message.Should().Contain("Handover").And.Contain("MacBook").And.Contain("GitHub").And.Contain("Final pay");
    }

    [Fact]
    public void Dragging_InProgress_to_Completed_applies_exactly_the_same_gates_as_PendingClearance_to_Completed()
    {
        var scenarios = new (IOffboardingTaskState[] Tasks, IOffboardingAssetState[] Assets, IOffboardingAccessState[] Access, IOffboardingSettlementState[] Settlement)[]
        {
            ([Done()], ClearedAssets, ClearedAccess, ClearedSettlement),
            ([Open("Handover")], ClearedAssets, ClearedAccess, ClearedSettlement),
            ([Done()], [Asset(OffboardingAssetStatuses.Missing)], ClearedAccess, ClearedSettlement),
            ([Done()], ClearedAssets, [Access(OffboardingAccessStatuses.Scheduled)], ClearedSettlement),
            ([Done()], ClearedAssets, ClearedAccess, [Settlement(OffboardingSettlementStatuses.Pending)]),
        };

        foreach (var s in scenarios)
        {
            var fast = OffboardingValidation.ValidateTransition("InProgress", "Completed", s.Tasks, null, s.Assets, s.Access, s.Settlement);
            var normal = OffboardingValidation.ValidateTransition("PendingClearance", "Completed", s.Tasks, null, s.Assets, s.Access, s.Settlement);
            fast.Should().Be(normal);
        }
    }

    [Fact]
    public void Completion_cannot_be_reached_by_omitting_the_gate_collections()
    {
        // Callers that pass no assets/access/settlement lists see only the task gate; the service always passes them for Completed.
        // With an open required task the transition must fail regardless.
        OffboardingValidation.ValidateTransition("InProgress", "Completed", [Open("Handover")]).Should().Contain("Handover");
    }

    [Theory]
    [InlineData("PendingReturn", true, false)]
    [InlineData("Damaged", true, false)]
    [InlineData("Missing", true, false)]
    [InlineData("Returned", true, true)]
    [InlineData("Waived", true, true)]
    [InlineData("PendingReturn", false, true)]
    [InlineData("Missing", false, true)]
    public void Asset_gate_requires_returned_or_waived_for_required_assets(string status, bool required, bool cleared)
    {
        OffboardingValidation.IsAssetCleared(Asset(status, required)).Should().Be(cleared);
    }

    [Theory]
    [InlineData("Pending", false)]
    [InlineData("Scheduled", false)]
    [InlineData("Revoked", true)]
    [InlineData("Verified", true)]
    [InlineData("NotApplicable", true)]
    public void Access_gate_requires_revoked_verified_or_not_applicable(string status, bool cleared)
    {
        OffboardingValidation.IsAccessCleared(Access(status)).Should().Be(cleared);
    }

    [Theory]
    [InlineData("Pending", true, false)]
    [InlineData("Completed", true, true)]
    [InlineData("Waived", true, true)]
    [InlineData("NotApplicable", true, true)]
    [InlineData("Pending", false, true)]
    public void Settlement_gate_requires_completed_waived_or_not_applicable_for_required_items(string status, bool required, bool cleared)
    {
        OffboardingValidation.IsSettlementCleared(Settlement(status, required)).Should().Be(cleared);
    }

    [Fact]
    public void Completion_blocker_labels_are_prefixed_by_area()
    {
        var labels = OffboardingValidation.GetCompletionBlockerLabels(
            [Open("Handover")],
            [Asset(OffboardingAssetStatuses.PendingReturn, name: "MacBook")],
            [Access(OffboardingAccessStatuses.Pending, "GitHub")],
            [Settlement(OffboardingSettlementStatuses.Pending, title: "Final pay")]);

        labels.Should().Equal("Task: Handover", "Asset: MacBook", "Access: GitHub", "Settlement: Final pay");
        OffboardingValidation.CanComplete([Done()], ClearedAssets, ClearedAccess, ClearedSettlement).Should().BeTrue();
        OffboardingValidation.CanComplete([Open()], ClearedAssets, ClearedAccess, ClearedSettlement).Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // Progress / overdue
    // -------------------------------------------------------------------------

    [Fact]
    public void Progress_is_completed_over_applicable_tasks()
    {
        OffboardingValidation.ComputeProgressPercent([Done(), Open(), Open(), Open()]).Should().Be(25);
        OffboardingValidation.ComputeProgressPercent([Done(), Done()]).Should().Be(100);
        OffboardingValidation.ComputeProgressPercent([Done(), Open(), Open()]).Should().Be(33.3);
    }

    [Fact]
    public void Progress_excludes_waived_and_cancelled_tasks()
    {
        var waived = new TestTask { IsWaived = true };
        var cancelled = new TestTask { Status = OffboardingTaskStatuses.Cancelled };

        OffboardingValidation.ComputeProgressPercent([Done(), waived, cancelled]).Should().Be(100);
        OffboardingValidation.CountProgress([Done(), Open(), waived, cancelled]).Should().Be((1, 2));
    }

    [Fact]
    public void Progress_is_zero_without_applicable_tasks()
    {
        OffboardingValidation.ComputeProgressPercent([]).Should().Be(0);
        OffboardingValidation.ComputeProgressPercent([new TestTask { IsWaived = true }]).Should().Be(0);
    }

    [Fact]
    public void Approval_gated_task_only_counts_once_approved()
    {
        var awaiting = new TestTask
        {
            Status = OffboardingTaskStatuses.Completed, RequiresApproval = true, ApprovalStatus = OffboardingApprovalStatuses.Pending
        };
        var approved = new TestTask
        {
            Status = OffboardingTaskStatuses.Completed, RequiresApproval = true, ApprovalStatus = OffboardingApprovalStatuses.Approved
        };

        OffboardingValidation.ComputeProgressPercent([awaiting, approved]).Should().Be(50);
    }

    [Fact]
    public void Overdue_rules()
    {
        var today = new DateTime(2026, 6, 10);

        OffboardingValidation.IsOverdue(new TestTask { DueDate = today.AddDays(-1) }, today).Should().BeTrue();
        OffboardingValidation.IsOverdue(new TestTask { DueDate = today }, today).Should().BeFalse();
        OffboardingValidation.IsOverdue(new TestTask { DueDate = today.AddDays(1) }, today).Should().BeFalse();
        OffboardingValidation.IsOverdue(new TestTask { DueDate = null }, today).Should().BeFalse();
        OffboardingValidation.IsOverdue(new TestTask { DueDate = today.AddDays(-3), Status = OffboardingTaskStatuses.Completed }, today).Should().BeFalse();
        OffboardingValidation.IsOverdue(new TestTask { DueDate = today.AddDays(-3), IsWaived = true }, today).Should().BeFalse();
        OffboardingValidation.IsOverdue(new TestTask { DueDate = today.AddDays(-3), Status = OffboardingTaskStatuses.Cancelled }, today).Should().BeFalse();

        OffboardingValidation.CountOverdue([
            new TestTask { DueDate = today.AddDays(-2) },
            new TestTask { DueDate = today.AddDays(-5), Status = OffboardingTaskStatuses.InProgress },
            new TestTask { DueDate = today.AddDays(2) }
        ], today).Should().Be(2);
    }

    [Fact]
    public void Dependencies_block_completion_until_satisfied()
    {
        var first = Open("Handover");
        var second = new TestTask { Title = "Revoke access", DependsOnTaskId = first.Id };

        OffboardingValidation.FindUnmetDependency(second, [first, second]).Should().Be("Handover");

        first.Status = OffboardingTaskStatuses.Completed;
        OffboardingValidation.FindUnmetDependency(second, [first, second]).Should().BeNull();

        first.Status = OffboardingTaskStatuses.Pending;
        first.IsWaived = true;
        OffboardingValidation.FindUnmetDependency(second, [first, second]).Should().BeNull();

        OffboardingValidation.FindUnmetDependency(first, [first, second]).Should().BeNull();
    }

    // -------------------------------------------------------------------------
    // Dates
    // -------------------------------------------------------------------------

    [Fact]
    public void Due_date_is_last_working_day_plus_offset()
    {
        var lastDay = new DateTime(2026, 6, 30, 15, 0, 0);

        OffboardingValidation.ComputeDueDate(lastDay, -5).Should().Be(new DateTime(2026, 6, 25));
        OffboardingValidation.ComputeDueDate(lastDay, 0).Should().Be(new DateTime(2026, 6, 30));
        OffboardingValidation.ComputeDueDate(lastDay, 3).Should().Be(new DateTime(2026, 7, 3));
        OffboardingValidation.ComputeDueDate(lastDay, null).Should().BeNull();
        OffboardingValidation.ComputeDueDate(null, -5).Should().BeNull();
    }

    [Fact]
    public void Percent_change_needs_a_baseline()
    {
        OffboardingValidation.PercentChange(10, 0).Should().BeNull();
        OffboardingValidation.PercentChange(15, 10).Should().Be(50);
        OffboardingValidation.PercentChange(5, 10).Should().Be(-50);
    }

    [Fact]
    public void Termination_effective_date_defaults_to_last_working_day()
    {
        var lwd = new DateTime(2026, 7, 1, 9, 30, 0);
        var explicitDate = new DateTime(2026, 7, 15);

        OffboardingValidation.ResolveTerminationEffectiveDate(null, lwd).Should().Be(new DateTime(2026, 7, 1));
        OffboardingValidation.ResolveTerminationEffectiveDate(explicitDate, lwd).Should().Be(explicitDate);
        OffboardingValidation.ResolveTerminationEffectiveDate(null, null).Should().BeNull();
    }

    [Fact]
    public void Termination_is_due_on_or_after_the_effective_date()
    {
        var today = new DateTime(2026, 7, 1, 18, 0, 0);

        OffboardingValidation.IsTerminationDue(new DateTime(2026, 6, 30), today).Should().BeTrue();
        OffboardingValidation.IsTerminationDue(new DateTime(2026, 7, 1), today).Should().BeTrue();
        OffboardingValidation.IsTerminationDue(new DateTime(2026, 7, 2), today).Should().BeFalse();
        OffboardingValidation.IsTerminationDue(null, today).Should().BeFalse();
    }

    [Fact]
    public void Separation_dates_must_be_in_order()
    {
        var notice = new DateTime(2026, 6, 1);
        var lastDay = new DateTime(2026, 6, 30);

        OffboardingValidation.ValidateSeparationDates(notice, lastDay, lastDay).Should().BeEmpty();
        OffboardingValidation.ValidateSeparationDates(notice, lastDay, new DateTime(2026, 7, 5)).Should().BeEmpty();
        OffboardingValidation.ValidateSeparationDates(null, null, null).Should().BeEmpty();

        OffboardingValidation.ValidateSeparationDates(lastDay, notice, null).Should().ContainSingle().Which.Should().Contain("last working day");
        OffboardingValidation.ValidateSeparationDates(notice, lastDay, new DateTime(2026, 6, 20)).Should().ContainSingle().Which.Should().Contain("effective date");
    }

    // -------------------------------------------------------------------------
    // Employee notification policy
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("InvoluntaryTermination", true)]
    [InlineData("Layoff", true)]
    [InlineData("layoff", true)]
    [InlineData("VoluntaryResignation", false)]
    [InlineData("ContractExpiration", false)]
    [InlineData("Retirement", false)]
    [InlineData("MutualAgreement", false)]
    [InlineData("Other", false)]
    [InlineData(null, false)]
    public void Involuntary_separations_are_employer_initiated(string? type, bool expected)
    {
        OffboardingValidation.IsInvoluntary(type).Should().Be(expected);
    }

    [Theory]
    [InlineData("VoluntaryResignation", true, true)]
    [InlineData("VoluntaryResignation", false, false)]   // never implicit
    [InlineData("Retirement", true, true)]
    [InlineData("InvoluntaryTermination", true, false)]  // never for involuntary, even when asked
    [InlineData("Layoff", true, false)]
    [InlineData("Layoff", false, false)]
    public void Employee_is_only_notified_on_explicit_opt_in_for_non_involuntary_separations(string type, bool flag, bool expected)
    {
        OffboardingValidation.CanNotifyEmployee(type, flag).Should().Be(expected);
    }

    [Fact]
    public void Opting_in_to_employee_notification_is_rejected_for_involuntary_separations()
    {
        OffboardingValidation.ValidateNotifyEmployee("InvoluntaryTermination", true).Should().Contain("involuntary");
        OffboardingValidation.ValidateNotifyEmployee("Layoff", true).Should().NotBeNull();
        OffboardingValidation.ValidateNotifyEmployee("InvoluntaryTermination", false).Should().BeNull();
        OffboardingValidation.ValidateNotifyEmployee("VoluntaryResignation", true).Should().BeNull();
    }

    // -------------------------------------------------------------------------
    // Access / asset / settlement / exit interview rules
    // -------------------------------------------------------------------------

    [Fact]
    public void Access_can_only_be_verified_after_it_was_revoked()
    {
        OffboardingValidation.ValidateAccessStatusChange("Pending", "Verified").Should().Contain("revoked");
        OffboardingValidation.ValidateAccessStatusChange("Scheduled", "Verified").Should().Contain("revoked");
        OffboardingValidation.ValidateAccessStatusChange("Revoked", "Verified").Should().BeNull();
        OffboardingValidation.ValidateAccessStatusChange("Pending", "Revoked").Should().BeNull();
        OffboardingValidation.ValidateAccessStatusChange("Pending", "Scheduled").Should().BeNull();
        OffboardingValidation.ValidateAccessStatusChange("Pending", "NotApplicable").Should().BeNull();
        OffboardingValidation.ValidateAccessStatusChange("Revoked", "Revoked").Should().Contain("already");
        OffboardingValidation.ValidateAccessStatusChange("Pending", "Bogus").Should().Contain("must be one of");
    }

    [Fact]
    public void Access_status_request_is_validated()
    {
        OffboardingValidation.ValidateAccessStatusRequest(new UpdateOffboardingAccessStatusRequest { Status = "Revoked" }).Should().BeEmpty();
        OffboardingValidation.ValidateAccessStatusRequest(new UpdateOffboardingAccessStatusRequest { Status = "Nope" }).Should().NotBeEmpty();
        OffboardingValidation.ValidateAccessStatusRequest(new UpdateOffboardingAccessStatusRequest { Status = "Revoked", Notes = new string('x', 1001) }).Should().NotBeEmpty();
    }

    [Fact]
    public void Waiving_an_asset_requires_a_reason()
    {
        OffboardingValidation.ValidateAssetStatusChange(new UpdateOffboardingAssetStatusRequest { Status = "Waived" })
            .Should().ContainSingle().Which.Should().Contain("reason");
        OffboardingValidation.ValidateAssetStatusChange(new UpdateOffboardingAssetStatusRequest { Status = "Waived", WaiveReason = "Lost in transit, written off" })
            .Should().BeEmpty();
        OffboardingValidation.ValidateAssetStatusChange(new UpdateOffboardingAssetStatusRequest { Status = "Returned" }).Should().BeEmpty();
        OffboardingValidation.ValidateAssetStatusChange(new UpdateOffboardingAssetStatusRequest { Status = "Nope" }).Should().NotBeEmpty();
    }

    [Fact]
    public void Asset_return_date_cannot_be_in_the_future()
    {
        var today = new DateTime(2026, 6, 10);

        OffboardingValidation.ValidateAssetStatusChange(
            new UpdateOffboardingAssetStatusRequest { Status = "Returned", ActualReturnDate = today.AddDays(1) }, today).Should().NotBeEmpty();
        OffboardingValidation.ValidateAssetStatusChange(
            new UpdateOffboardingAssetStatusRequest { Status = "Returned", ActualReturnDate = today }, today).Should().BeEmpty();
    }

    [Fact]
    public void Asset_and_access_fields_are_validated()
    {
        OffboardingValidation.ValidateAssetFields("Laptop", "Hardware", "SN-1", null).Should().BeEmpty();
        OffboardingValidation.ValidateAssetFields("  ", null, null, null).Should().NotBeEmpty();
        OffboardingValidation.ValidateAssetFields(new string('x', 201), null, null, null).Should().NotBeEmpty();
        OffboardingValidation.ValidateAssetFields("Laptop", new string('x', 81), null, null).Should().NotBeEmpty();

        OffboardingValidation.ValidateAccessFields("GitHub", null).Should().BeEmpty();
        OffboardingValidation.ValidateAccessFields("", null).Should().NotBeEmpty();
        OffboardingValidation.ValidateAccessFields(new string('x', 201), null).Should().NotBeEmpty();
    }

    [Fact]
    public void Settlement_update_is_validated()
    {
        OffboardingValidation.ValidateSettlementUpdate(new UpdateOffboardingSettlementItemRequest { Status = "Completed", Amount = 1200.5m, Currency = "usd" })
            .Should().BeEmpty();
        OffboardingValidation.ValidateSettlementUpdate(new UpdateOffboardingSettlementItemRequest { Status = "Bogus" }).Should().NotBeEmpty();
        OffboardingValidation.ValidateSettlementUpdate(new UpdateOffboardingSettlementItemRequest { Status = "Pending", Amount = -1m }).Should().NotBeEmpty();
        OffboardingValidation.ValidateSettlementUpdate(new UpdateOffboardingSettlementItemRequest { Status = "Pending", Currency = "DOLLARS" }).Should().NotBeEmpty();
        OffboardingValidation.ValidateSettlementUpdate(new UpdateOffboardingSettlementItemRequest { Status = "Pending", Currency = "U1D" }).Should().NotBeEmpty();
    }

    [Fact]
    public void Exit_interview_is_validated()
    {
        OffboardingValidation.ValidateExitInterview(new UpdateOffboardingExitInterviewRequest { Status = "NotScheduled" }).Should().BeEmpty();
        OffboardingValidation.ValidateExitInterview(new UpdateOffboardingExitInterviewRequest
        {
            Status = "Scheduled", ScheduledAt = DateTimeOffset.UtcNow.AddDays(2)
        }).Should().BeEmpty();
        OffboardingValidation.ValidateExitInterview(new UpdateOffboardingExitInterviewRequest { Status = "Scheduled" })
            .Should().ContainSingle().Which.Should().Contain("scheduled date");
        OffboardingValidation.ValidateExitInterview(new UpdateOffboardingExitInterviewRequest { Status = "Bogus" }).Should().NotBeEmpty();
        OffboardingValidation.ValidateExitInterview(new UpdateOffboardingExitInterviewRequest { Status = "Completed", ResponsesJson = "{not json" })
            .Should().ContainSingle().Which.Should().Contain("valid JSON");
        OffboardingValidation.ValidateExitInterview(new UpdateOffboardingExitInterviewRequest { Status = "Completed", ResponsesJson = "{\"q1\":\"fine\"}" })
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("{}", true)]
    [InlineData("[1,2]", true)]
    [InlineData("{broken", false)]
    public void Json_fields_must_be_valid_or_empty(string? json, bool expected)
    {
        OffboardingValidation.IsValidJsonOrEmpty(json).Should().Be(expected);
    }

    [Fact]
    public void Settlement_status_lookup_is_case_insensitive()
    {
        OffboardingValidation.NormalizeSettlementStatus("completed").Should().Be("Completed");
        OffboardingValidation.NormalizeSettlementStatus(" waived ").Should().Be("Waived");
        OffboardingValidation.NormalizeSettlementStatus("nope").Should().BeNull();
        OffboardingValidation.NormalizeSeparationType("layoff").Should().Be("Layoff");
        OffboardingValidation.NormalizeStatus("pendingclearance").Should().Be("PendingClearance");
    }

    // -------------------------------------------------------------------------
    // Templates
    // -------------------------------------------------------------------------

    private static UpsertOffboardingTemplateRequest ValidTemplate() => new()
    {
        Name = "Voluntary Resignation",
        SeparationType = "VoluntaryResignation",
        Tasks =
        [
            new UpsertOffboardingTemplateTaskRequest { Title = "Collect resignation letter", DueOffsetDays = -14 },
            new UpsertOffboardingTemplateTaskRequest { Title = "Revoke access", Category = OffboardingTaskCategories.ITSecurity, AssignmentType = OffboardingAssignmentTypes.IT }
        ]
    };

    [Fact]
    public void Valid_template_passes()
    {
        OffboardingValidation.ValidateTemplate(ValidTemplate()).Should().BeEmpty();
    }

    [Fact]
    public void Template_requires_a_name_and_a_known_separation_type()
    {
        var template = ValidTemplate();
        template.Name = " ";
        OffboardingValidation.ValidateTemplate(template).Should().Contain(e => e.Contains("name is required"));

        template = ValidTemplate();
        template.SeparationType = "Retired-ish";
        OffboardingValidation.ValidateTemplate(template).Should().Contain(e => e.Contains("Separation type"));
    }

    [Fact]
    public void Default_template_must_be_active()
    {
        var template = ValidTemplate();
        template.IsDefault = true;
        template.IsActive = false;

        OffboardingValidation.ValidateTemplate(template).Should().Contain(e => e.Contains("default template must be active"));
    }

    [Fact]
    public void Template_tasks_are_validated()
    {
        var template = ValidTemplate();
        template.Tasks.Add(new UpsertOffboardingTemplateTaskRequest { Title = "" });
        template.Tasks.Add(new UpsertOffboardingTemplateTaskRequest { Title = "Bad category", Category = "Whatever" });
        template.Tasks.Add(new UpsertOffboardingTemplateTaskRequest { Title = "Specific user missing", AssignmentType = OffboardingAssignmentTypes.SpecificUser });
        template.Tasks.Add(new UpsertOffboardingTemplateTaskRequest { Title = "Doc without category", RequiresDocument = true });
        template.Tasks.Add(new UpsertOffboardingTemplateTaskRequest { Title = "Way too early", DueOffsetDays = -1000 });

        var errors = OffboardingValidation.ValidateTemplate(template);

        errors.Should().Contain(e => e.Contains("title is required"));
        errors.Should().Contain(e => e.Contains("category must be one of"));
        errors.Should().Contain(e => e.Contains("specific user"));
        errors.Should().Contain(e => e.Contains("document category is required"));
        errors.Should().Contain(e => e.Contains("due offset"));
    }

    [Fact]
    public void Template_dependency_cycles_and_unknown_references_are_rejected()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var cyclic = ValidTemplate();
        cyclic.Tasks =
        [
            new UpsertOffboardingTemplateTaskRequest { Id = a, Title = "A", DependsOnTaskId = b },
            new UpsertOffboardingTemplateTaskRequest { Id = b, Title = "B", DependsOnTaskId = a }
        ];
        OffboardingValidation.ValidateTemplate(cyclic).Should().Contain(e => e.Contains("cycle"));

        var self = ValidTemplate();
        self.Tasks = [new UpsertOffboardingTemplateTaskRequest { Id = a, Title = "A", DependsOnTaskId = a }];
        OffboardingValidation.ValidateTemplate(self).Should().Contain(e => e.Contains("cannot depend on itself"));

        var unknown = ValidTemplate();
        unknown.Tasks = [new UpsertOffboardingTemplateTaskRequest { Id = a, Title = "A", DependsOnTaskId = Guid.NewGuid() }];
        OffboardingValidation.ValidateTemplate(unknown).Should().Contain(e => e.Contains("not part of this template"));

        var linear = ValidTemplate();
        linear.Tasks =
        [
            new UpsertOffboardingTemplateTaskRequest { Id = a, Title = "A" },
            new UpsertOffboardingTemplateTaskRequest { Id = b, Title = "B", DependsOnTaskId = a }
        ];
        OffboardingValidation.ValidateTemplate(linear).Should().BeEmpty();
    }

    [Fact]
    public void Dependency_cycle_detection()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();

        OffboardingValidation.HasDependencyCycle(new Dictionary<Guid, Guid?> { [a] = b, [b] = c, [c] = null }).Should().BeFalse();
        OffboardingValidation.HasDependencyCycle(new Dictionary<Guid, Guid?> { [a] = b, [b] = c, [c] = a }).Should().BeTrue();
        OffboardingValidation.HasDependencyCycle(new Dictionary<Guid, Guid?> { [a] = a }).Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // Custom tasks
    // -------------------------------------------------------------------------

    [Fact]
    public void Custom_task_validation()
    {
        OffboardingValidation.ValidateCustomTask(new CreateOffboardingTaskRequest
        {
            Title = "Return badge", Category = OffboardingTaskCategories.EquipmentAssets, AssignmentType = OffboardingAssignmentTypes.HR
        }).Should().BeEmpty();

        OffboardingValidation.ValidateCustomTask(new CreateOffboardingTaskRequest { Title = "", AssignmentType = "HR" }).Should().NotBeEmpty();
        OffboardingValidation.ValidateCustomTask(new CreateOffboardingTaskRequest { Title = "Assigned", AssignmentType = "SpecificUser" })
            .Should().Contain(e => e.Contains("specific user"));
    }

    [Fact]
    public void Task_update_validation_restricts_status_and_requires_a_waive_reason()
    {
        UpdateOffboardingTaskRequest Valid() => new() { Title = "T", AssignmentType = "HR" };

        OffboardingValidation.ValidateTaskUpdate(Valid()).Should().BeEmpty();

        var request = Valid();
        request.Status = "Completed";
        OffboardingValidation.ValidateTaskUpdate(request).Should().Contain(e => e.Contains("Pending, InProgress or Blocked"));

        request = Valid();
        request.Status = "Blocked";
        OffboardingValidation.ValidateTaskUpdate(request).Should().BeEmpty();

        request = Valid();
        request.IsWaived = true;
        OffboardingValidation.ValidateTaskUpdate(request).Should().Contain(e => e.Contains("reason is required to waive"));

        request = Valid();
        request.IsWaived = true;
        request.WaiveReason = "No company laptop was issued";
        OffboardingValidation.ValidateTaskUpdate(request).Should().BeEmpty();
    }

    // -------------------------------------------------------------------------
    // Offboarding requests
    // -------------------------------------------------------------------------

    private static CreateOffboardingRequest ValidCreate() => new()
    {
        EmployeeId = Guid.NewGuid(),
        SeparationType = "VoluntaryResignation",
        NoticeDate = new DateTime(2026, 6, 1),
        LastWorkingDay = new DateTime(2026, 6, 30)
    };

    [Fact]
    public void Valid_create_request_passes()
    {
        OffboardingValidation.ValidateCreateOffboarding(ValidCreate()).Should().BeEmpty();
    }

    [Fact]
    public void Create_requires_employee_and_a_known_separation_type()
    {
        var request = ValidCreate();
        request.EmployeeId = Guid.Empty;
        OffboardingValidation.ValidateCreateOffboarding(request).Should().Contain(e => e.Contains("employeeId"));

        request = ValidCreate();
        request.SeparationType = "";
        OffboardingValidation.ValidateCreateOffboarding(request).Should().Contain(e => e.Contains("separationType is required"));

        request = ValidCreate();
        request.SeparationType = "Fired";
        OffboardingValidation.ValidateCreateOffboarding(request).Should().Contain(e => e.Contains("Separation type must be one of"));

        request = ValidCreate();
        request.SeparationType = "voluntaryresignation";
        OffboardingValidation.ValidateCreateOffboarding(request).Should().BeEmpty();
    }

    [Fact]
    public void Create_rejects_employee_notification_for_involuntary_separations()
    {
        var request = ValidCreate();
        request.SeparationType = "InvoluntaryTermination";
        request.NotifyEmployee = true;
        OffboardingValidation.ValidateCreateOffboarding(request).Should().Contain(e => e.Contains("involuntary"));

        request.NotifyEmployee = false;
        OffboardingValidation.ValidateCreateOffboarding(request).Should().BeEmpty();

        request = ValidCreate();
        request.NotifyEmployee = true;
        OffboardingValidation.ValidateCreateOffboarding(request).Should().BeEmpty();
    }

    [Fact]
    public void Create_validates_dates_lengths_and_nested_items()
    {
        var request = ValidCreate();
        request.LastWorkingDay = new DateTime(2026, 5, 1);
        OffboardingValidation.ValidateCreateOffboarding(request).Should().Contain(e => e.Contains("last working day"));

        request = ValidCreate();
        request.TerminationEffectiveDate = new DateTime(2026, 6, 15);
        OffboardingValidation.ValidateCreateOffboarding(request).Should().Contain(e => e.Contains("effective date"));

        request = ValidCreate();
        request.SeparationReason = new string('x', 1001);
        request.ConfidentialNotes = new string('x', 20001);
        request.RehireEligibility = "Maybe";
        OffboardingValidation.ValidateCreateOffboarding(request).Should().HaveCount(3);

        request = ValidCreate();
        request.AdditionalTasks = [new CreateOffboardingTaskRequest { Title = "", AssignmentType = OffboardingAssignmentTypes.HR }];
        request.Assets = [new CreateOffboardingAssetRequest { AssetName = "" }];
        request.AccessSystems = ["GitHub", " "];
        OffboardingValidation.ValidateCreateOffboarding(request).Should().HaveCount(3);
    }

    [Fact]
    public void Start_for_employee_requires_a_separation_type()
    {
        OffboardingValidation.ValidateStartForEmployee(new StartOffboardingForEmployeeRequest()).Should().NotBeEmpty();
        OffboardingValidation.ValidateStartForEmployee(new StartOffboardingForEmployeeRequest { SeparationType = "Nope" }).Should().NotBeEmpty();
        OffboardingValidation.ValidateStartForEmployee(new StartOffboardingForEmployeeRequest { SeparationType = "Retirement" }).Should().BeEmpty();
    }

    [Fact]
    public void Update_request_validation()
    {
        OffboardingValidation.ValidateUpdateOffboarding(new UpdateOffboardingRequest()).Should().BeEmpty();
        OffboardingValidation.ValidateUpdateOffboarding(new UpdateOffboardingRequest { SeparationType = "Nope" }).Should().NotBeEmpty();
        OffboardingValidation.ValidateUpdateOffboarding(new UpdateOffboardingRequest { RehireEligibility = "Maybe" }).Should().NotBeEmpty();
        OffboardingValidation.ValidateUpdateOffboarding(new UpdateOffboardingRequest
        {
            NoticeDate = new DateTime(2026, 6, 10), LastWorkingDay = new DateTime(2026, 6, 1)
        }).Should().NotBeEmpty();
    }
}
