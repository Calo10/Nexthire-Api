using FluentAssertions;
using nexthire_api.DTOs.CoreHr;
using nexthire_api.Services;
using Xunit;

namespace nexthire_api.Tests;

public class OnboardingValidationTests
{
    private sealed class TestTask : IOnboardingTaskState
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Title { get; set; } = "Task";
        public string Status { get; set; } = OnboardingTaskStatuses.Pending;
        public bool IsWaived { get; set; }
        public bool IsRequired { get; set; } = true;
        public bool RequiresApproval { get; set; }
        public string? ApprovalStatus { get; set; }
        public DateTime? DueDate { get; set; }
        public Guid? DependsOnTaskId { get; set; }
    }

    private static TestTask Done(string title = "Done", bool required = true) =>
        new() { Title = title, Status = OnboardingTaskStatuses.Completed, IsRequired = required };

    private static TestTask Open(string title = "Open", bool required = true) =>
        new() { Title = title, Status = OnboardingTaskStatuses.Pending, IsRequired = required };

    // -------------------------------------------------------------------------
    // Status transitions
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("NotStarted", "InProgress", true)]
    [InlineData("InProgress", "InReview", true)]
    [InlineData("InReview", "Completed", true)]
    [InlineData("InReview", "InProgress", true)]
    [InlineData("InProgress", "Completed", true)]   // Kanban fast path (same validation)
    [InlineData("NotStarted", "Cancelled", true)]
    [InlineData("InProgress", "Cancelled", true)]
    [InlineData("InReview", "Cancelled", true)]
    [InlineData("NotStarted", "InReview", false)]
    [InlineData("NotStarted", "Completed", false)]
    [InlineData("InProgress", "NotStarted", false)]
    [InlineData("InReview", "NotStarted", false)]
    [InlineData("Completed", "InProgress", false)]
    [InlineData("Completed", "Cancelled", false)]
    [InlineData("Cancelled", "InProgress", false)]
    [InlineData("Cancelled", "NotStarted", false)]
    [InlineData("InProgress", "InProgress", false)]
    [InlineData("InProgress", "Bogus", false)]
    [InlineData(null, "InProgress", false)]
    public void CanTransition_follows_the_status_machine(string? from, string? to, bool expected)
    {
        OnboardingValidation.CanTransition(from, to).Should().Be(expected);
    }

    [Fact]
    public void Starting_does_not_depend_on_task_state()
    {
        OnboardingValidation.ValidateTransition("NotStarted", "InProgress", new[] { Open() }).Should().BeNull();
    }

    [Theory]
    [InlineData("InProgress", "InReview")]
    [InlineData("InReview", "Completed")]
    [InlineData("InProgress", "Completed")]
    public void Review_and_completion_are_rejected_while_a_required_task_is_incomplete(string from, string to)
    {
        var tasks = new[] { Done("Contract"), Open("Provide ID") };

        var error = OnboardingValidation.ValidateTransition(from, to, tasks);

        error.Should().NotBeNull();
        error.Should().Contain("Provide ID");
    }

    [Theory]
    [InlineData("InProgress", "InReview")]
    [InlineData("InReview", "Completed")]
    [InlineData("InProgress", "Completed")]
    public void Review_and_completion_are_allowed_when_all_required_tasks_are_done(string from, string to)
    {
        var tasks = new[] { Done(), Done(), Open("Optional extra", required: false) };

        OnboardingValidation.ValidateTransition(from, to, tasks).Should().BeNull();
    }

    [Fact]
    public void Drag_to_Completed_uses_the_same_gate_as_Complete()
    {
        var tasks = new[] { Open("Sign contract") };

        // Same function and same outcome regardless of the source column.
        var fromProgress = OnboardingValidation.ValidateTransition("InProgress", "Completed", tasks);
        var fromReview = OnboardingValidation.ValidateTransition("InReview", "Completed", tasks);

        fromProgress.Should().NotBeNull();
        fromReview.Should().NotBeNull();
        fromProgress.Should().Be(fromReview);
    }

    [Fact]
    public void Returning_from_review_to_in_progress_needs_no_task_check()
    {
        OnboardingValidation.ValidateTransition("InReview", "InProgress", new[] { Open() }).Should().BeNull();
    }

    [Fact]
    public void Cancelling_requires_a_reason()
    {
        OnboardingValidation.ValidateTransition("InProgress", "Cancelled", new[] { Open() }, null).Should().Contain("reason");
        OnboardingValidation.ValidateTransition("InProgress", "Cancelled", new[] { Open() }, "  ").Should().Contain("reason");
        OnboardingValidation.ValidateTransition("InProgress", "Cancelled", new[] { Open() }, "Candidate withdrew").Should().BeNull();
    }

    [Fact]
    public void Terminal_and_unknown_statuses_are_rejected_with_a_message()
    {
        OnboardingValidation.ValidateTransition("Completed", "InProgress", []).Should().Contain("Completed");
        OnboardingValidation.ValidateTransition("Cancelled", "InProgress", []).Should().Contain("Cancelled");
        OnboardingValidation.ValidateTransition("InProgress", "Nope", []).Should().Contain("Status must be one of");
        OnboardingValidation.ValidateTransition("InProgress", "InProgress", []).Should().Contain("already");
    }

    [Fact]
    public void Status_normalization_is_case_insensitive_and_returns_canonical_casing()
    {
        OnboardingValidation.NormalizeStatus("inprogress").Should().Be("InProgress");
        OnboardingValidation.NormalizeStatus(" COMPLETED ").Should().Be("Completed");
        OnboardingValidation.NormalizeStatus("done").Should().BeNull();
        OnboardingValidation.NormalizeStatus(null).Should().BeNull();
        OnboardingValidation.IsActiveStatus("InReview").Should().BeTrue();
        OnboardingValidation.IsActiveStatus("Cancelled").Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // Progress
    // -------------------------------------------------------------------------

    [Fact]
    public void Progress_is_completed_over_applicable_times_100()
    {
        var tasks = new[] { Done(), Done(), Open(), Open() };

        OnboardingValidation.ComputeProgressPercent(tasks).Should().Be(50);
        OnboardingValidation.CountProgress(tasks).Should().Be((2, 4));
    }

    [Fact]
    public void Progress_rounds_to_one_decimal()
    {
        OnboardingValidation.ComputeProgressPercent(new[] { Done(), Open(), Open() }).Should().Be(33.3);
        OnboardingValidation.ComputeProgressPercent(new[] { Done(), Done(), Open() }).Should().Be(66.7);
    }

    [Fact]
    public void Progress_excludes_waived_and_cancelled_tasks()
    {
        var tasks = new[]
        {
            Done(),
            Open(),
            new TestTask { Status = OnboardingTaskStatuses.Pending, IsWaived = true },
            new TestTask { Status = OnboardingTaskStatuses.Cancelled }
        };

        OnboardingValidation.CountProgress(tasks).Should().Be((1, 2));
        OnboardingValidation.ComputeProgressPercent(tasks).Should().Be(50);
    }

    [Fact]
    public void Progress_with_no_applicable_tasks_is_zero()
    {
        OnboardingValidation.ComputeProgressPercent([]).Should().Be(0);
        OnboardingValidation.ComputeProgressPercent(new[] { new TestTask { IsWaived = true } }).Should().Be(0);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("Pending", false)]
    [InlineData("Rejected", false)]
    [InlineData("Approved", true)]
    public void Task_requiring_approval_only_counts_when_approved(string? approval, bool counts)
    {
        var task = new TestTask
        {
            Status = OnboardingTaskStatuses.Completed,
            RequiresApproval = true,
            ApprovalStatus = approval
        };

        OnboardingValidation.IsSatisfied(task).Should().Be(counts);
        OnboardingValidation.CountProgress(new[] { task }).Completed.Should().Be(counts ? 1 : 0);
    }

    [Fact]
    public void Completed_task_without_approval_requirement_always_counts()
    {
        OnboardingValidation.IsSatisfied(new TestTask { Status = "Completed", RequiresApproval = false, ApprovalStatus = null })
            .Should().BeTrue();
    }

    [Fact]
    public void Required_task_awaiting_approval_blocks_review_until_approved()
    {
        var task = new TestTask
        {
            Title = "Sign contract",
            Status = OnboardingTaskStatuses.Completed,
            RequiresApproval = true,
            ApprovalStatus = OnboardingApprovalStatuses.Pending
        };

        OnboardingValidation.CanComplete(new[] { task }).Should().BeFalse();
        OnboardingValidation.ValidateTransition("InProgress", "InReview", new[] { task }).Should().Contain("Sign contract");

        task.ApprovalStatus = OnboardingApprovalStatuses.Approved;
        OnboardingValidation.CanComplete(new[] { task }).Should().BeTrue();
        OnboardingValidation.ValidateTransition("InProgress", "InReview", new[] { task }).Should().BeNull();
    }

    [Fact]
    public void Waived_or_cancelled_required_tasks_do_not_block_completion()
    {
        var tasks = new[]
        {
            Done(),
            new TestTask { Title = "Waived", IsWaived = true, Status = OnboardingTaskStatuses.Pending },
            new TestTask { Title = "Cancelled", Status = OnboardingTaskStatuses.Cancelled }
        };

        OnboardingValidation.GetCompletionBlockers(tasks).Should().BeEmpty();
        OnboardingValidation.CanComplete(tasks).Should().BeTrue();
    }

    [Fact]
    public void Optional_open_tasks_do_not_block_completion()
    {
        OnboardingValidation.CanComplete(new[] { Done(), Open("Nice to have", required: false) }).Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // Overdue / due dates / dependencies
    // -------------------------------------------------------------------------

    [Fact]
    public void Overdue_counts_only_open_applicable_tasks_past_due()
    {
        var today = new DateTime(2026, 11, 25);
        var tasks = new IOnboardingTaskState[]
        {
            new TestTask { DueDate = today.AddDays(-1) },                                            // overdue
            new TestTask { DueDate = today.AddDays(-1), Status = OnboardingTaskStatuses.Completed }, // done
            new TestTask { DueDate = today.AddDays(-1), IsWaived = true },                           // waived
            new TestTask { DueDate = today.AddDays(-1), Status = OnboardingTaskStatuses.Cancelled }, // cancelled
            new TestTask { DueDate = today },                                                        // due today: not overdue
            new TestTask { DueDate = null }                                                          // no due date
        };

        OnboardingValidation.CountOverdue(tasks, today).Should().Be(1);
    }

    [Fact]
    public void Due_date_is_start_plus_offset()
    {
        var start = new DateTime(2026, 12, 1, 14, 30, 0);

        OnboardingValidation.ComputeDueDate(start, 5).Should().Be(new DateTime(2026, 12, 6));
        OnboardingValidation.ComputeDueDate(start, -2).Should().Be(new DateTime(2026, 11, 29));
        OnboardingValidation.ComputeDueDate(start, 0).Should().Be(new DateTime(2026, 12, 1));
        OnboardingValidation.ComputeDueDate(start, null).Should().BeNull();
        OnboardingValidation.ComputeDueDate(null, 5).Should().BeNull();
    }

    [Fact]
    public void Task_with_unmet_dependency_is_blocked_until_the_dependency_is_satisfied()
    {
        var first = Open("Sign contract");
        var second = new TestTask { Title = "Create accounts", DependsOnTaskId = first.Id };
        var all = new IOnboardingTaskState[] { first, second };

        OnboardingValidation.FindUnmetDependency(second, all).Should().Be("Sign contract");

        first.Status = OnboardingTaskStatuses.Completed;
        OnboardingValidation.FindUnmetDependency(second, all).Should().BeNull();
    }

    [Fact]
    public void Waived_or_missing_dependency_does_not_block()
    {
        var waived = new TestTask { IsWaived = true };
        var task = new TestTask { DependsOnTaskId = waived.Id };
        var orphan = new TestTask { DependsOnTaskId = Guid.NewGuid() };

        OnboardingValidation.FindUnmetDependency(task, new IOnboardingTaskState[] { waived, task }).Should().BeNull();
        OnboardingValidation.FindUnmetDependency(orphan, new IOnboardingTaskState[] { orphan }).Should().BeNull();
    }

    [Fact]
    public void Percent_change_needs_a_baseline()
    {
        OnboardingValidation.PercentChange(12, 10).Should().Be(20);
        OnboardingValidation.PercentChange(5, 10).Should().Be(-50);
        OnboardingValidation.PercentChange(5, 0).Should().BeNull();
    }

    // -------------------------------------------------------------------------
    // Request validation
    // -------------------------------------------------------------------------

    private static UpsertOnboardingTemplateRequest ValidTemplate() => new()
    {
        Name = "General",
        Tasks =
        [
            new UpsertOnboardingTemplateTaskRequest { Title = "Sign contract", Category = "Documentation", AssignmentType = "HR" },
            new UpsertOnboardingTemplateTaskRequest { Title = "Laptop", Category = "ITEquipment", AssignmentType = "IT", DueOffsetDays = -1 }
        ]
    };

    [Fact]
    public void Valid_template_has_no_errors()
    {
        OnboardingValidation.ValidateTemplate(ValidTemplate()).Should().BeEmpty();
    }

    [Fact]
    public void Template_requires_a_name_and_valid_task_fields()
    {
        var request = ValidTemplate();
        request.Name = " ";
        request.Tasks[0].Title = "";
        request.Tasks[1].Category = "Nonsense";
        request.Tasks[1].AssignmentType = "Nobody";

        var errors = OnboardingValidation.ValidateTemplate(request);

        errors.Should().Contain(e => e.Contains("name is required"));
        errors.Should().Contain(e => e.Contains("Task 1") && e.Contains("title"));
        errors.Should().Contain(e => e.Contains("Task 2") && e.Contains("category"));
        errors.Should().Contain(e => e.Contains("Task 2") && e.Contains("assignment type"));
    }

    [Fact]
    public void SpecificUser_assignment_and_required_documents_need_supporting_fields()
    {
        var request = ValidTemplate();
        request.Tasks[0].AssignmentType = "SpecificUser";
        request.Tasks[1].RequiresDocument = true;

        var errors = OnboardingValidation.ValidateTemplate(request);

        errors.Should().Contain(e => e.Contains("Task 1") && e.Contains("specific user"));
        errors.Should().Contain(e => e.Contains("Task 2") && e.Contains("document category"));

        request.Tasks[0].DefaultAssigneeUserId = Guid.NewGuid();
        request.Tasks[1].DocumentCategory = "Contract";
        OnboardingValidation.ValidateTemplate(request).Should().BeEmpty();
    }

    [Fact]
    public void Template_default_must_be_active()
    {
        var request = ValidTemplate();
        request.IsDefault = true;
        request.IsActive = false;

        OnboardingValidation.ValidateTemplate(request).Should().Contain(e => e.Contains("default template must be active"));
    }

    [Fact]
    public void Template_dependencies_must_reference_tasks_in_the_payload_and_not_cycle()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var request = ValidTemplate();
        request.Tasks[0].Id = a;
        request.Tasks[1].Id = b;

        request.Tasks[1].DependsOnTaskId = a;
        OnboardingValidation.ValidateTemplate(request).Should().BeEmpty();

        request.Tasks[1].DependsOnTaskId = Guid.NewGuid();
        OnboardingValidation.ValidateTemplate(request).Should().Contain(e => e.Contains("not part of this template"));

        request.Tasks[1].DependsOnTaskId = b;
        OnboardingValidation.ValidateTemplate(request).Should().Contain(e => e.Contains("cannot depend on itself"));

        request.Tasks[0].DependsOnTaskId = b;
        request.Tasks[1].DependsOnTaskId = a;
        OnboardingValidation.ValidateTemplate(request).Should().Contain(e => e.Contains("cycle"));
    }

    [Fact]
    public void Dependency_cycle_detection_handles_chains_and_loops()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();

        OnboardingValidation.HasDependencyCycle(new Dictionary<Guid, Guid?> { [a] = null, [b] = a, [c] = b }).Should().BeFalse();
        OnboardingValidation.HasDependencyCycle(new Dictionary<Guid, Guid?> { [a] = c, [b] = a, [c] = b }).Should().BeTrue();
        OnboardingValidation.HasDependencyCycle(new Dictionary<Guid, Guid?> { [a] = a }).Should().BeTrue();
    }

    [Fact]
    public void Create_onboarding_validates_employee_dates_and_custom_tasks()
    {
        var request = new CreateOnboardingRequest
        {
            EmployeeId = Guid.Empty,
            StartDate = new DateTime(2026, 12, 10),
            ExpectedCompletionDate = new DateTime(2026, 12, 1),
            AdditionalTasks = [new CreateOnboardingTaskRequest { Title = "", Category = "Other", AssignmentType = "HR" }]
        };

        var errors = OnboardingValidation.ValidateCreateOnboarding(request);

        errors.Should().Contain(e => e.Contains("employeeId"));
        errors.Should().Contain(e => e.Contains("before the start date"));
        errors.Should().Contain(e => e.Contains("title is required"));

        request.EmployeeId = Guid.NewGuid();
        request.ExpectedCompletionDate = new DateTime(2026, 12, 31);
        request.AdditionalTasks = [new CreateOnboardingTaskRequest { Title = "Parking pass", Category = "Administrative", AssignmentType = "HR" }];
        OnboardingValidation.ValidateCreateOnboarding(request).Should().BeEmpty();
    }

    [Fact]
    public void Task_update_only_allows_pending_in_progress_or_blocked_as_direct_statuses()
    {
        var request = new UpdateOnboardingTaskRequest { Title = "Laptop", Category = "ITEquipment", AssignmentType = "IT", Status = "Blocked" };
        OnboardingValidation.ValidateTaskUpdate(request).Should().BeEmpty();

        request.Status = "Completed";
        OnboardingValidation.ValidateTaskUpdate(request).Should().Contain(e => e.Contains("Pending, InProgress or Blocked"));

        request.Status = "Cancelled";
        OnboardingValidation.ValidateTaskUpdate(request).Should().NotBeEmpty();
    }
}
