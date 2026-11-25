using FluentAssertions;
using nexthire_api.Security;
using Xunit;

namespace nexthire_api.Tests;

public class OnboardingPermissionsTests
{
    private static readonly string[] AllCapabilities =
    [
        OnboardingPermissions.View, OnboardingPermissions.Create, OnboardingPermissions.Edit,
        OnboardingPermissions.Cancel, OnboardingPermissions.Complete, OnboardingPermissions.ManageTemplates,
        OnboardingPermissions.ManageTasks, OnboardingPermissions.ApproveTasks,
        OnboardingPermissions.ManageDocuments, OnboardingPermissions.ManageAutomations
    ];

    [Fact]
    public void Capability_strings_use_the_Onboarding_prefix()
    {
        OnboardingPermissions.All.Should().BeEquivalentTo(AllCapabilities);
        OnboardingPermissions.All.Should().OnlyContain(c => c.StartsWith("Onboarding."));
        OnboardingPermissions.View.Should().Be("Onboarding.View");
        OnboardingPermissions.ManageAutomations.Should().Be("Onboarding.ManageAutomations");
    }

    [Theory]
    [InlineData("account_admin")]
    [InlineData("supervisor")]
    public void Admin_and_supervisor_have_every_capability(string role)
    {
        foreach (var capability in AllCapabilities)
            OnboardingPermissions.Has([role], capability).Should().BeTrue($"{role} should have {capability}");
    }

    [Fact]
    public void Recruiter_can_view_create_edit_and_manage_tasks_only()
    {
        string[] roles = ["recruiter"];

        OnboardingPermissions.Has(roles, OnboardingPermissions.View).Should().BeTrue();
        OnboardingPermissions.Has(roles, OnboardingPermissions.Create).Should().BeTrue();
        OnboardingPermissions.Has(roles, OnboardingPermissions.Edit).Should().BeTrue();
        OnboardingPermissions.Has(roles, OnboardingPermissions.ManageTasks).Should().BeTrue();

        OnboardingPermissions.Has(roles, OnboardingPermissions.Cancel).Should().BeFalse();
        OnboardingPermissions.Has(roles, OnboardingPermissions.Complete).Should().BeFalse();
        OnboardingPermissions.Has(roles, OnboardingPermissions.ManageTemplates).Should().BeFalse();
        OnboardingPermissions.Has(roles, OnboardingPermissions.ApproveTasks).Should().BeFalse();
        OnboardingPermissions.Has(roles, OnboardingPermissions.ManageDocuments).Should().BeFalse();
        OnboardingPermissions.Has(roles, OnboardingPermissions.ManageAutomations).Should().BeFalse();

        OnboardingPermissions.ListFor(roles).Should().BeEquivalentTo(
        [
            OnboardingPermissions.View, OnboardingPermissions.Create,
            OnboardingPermissions.Edit, OnboardingPermissions.ManageTasks
        ]);
    }

    [Fact]
    public void Sales_has_no_onboarding_capabilities()
    {
        foreach (var capability in AllCapabilities)
            OnboardingPermissions.Has(["sales"], capability).Should().BeFalse($"sales must not have {capability}");

        OnboardingPermissions.ListFor(["sales"]).Should().BeEmpty();
    }

    [Fact]
    public void Role_codes_are_case_insensitive_and_trimmed()
    {
        OnboardingPermissions.Has(["  Account_Admin "], OnboardingPermissions.ApproveTasks).Should().BeTrue();
        OnboardingPermissions.Has(["RECRUITER"], OnboardingPermissions.Create).Should().BeTrue();
    }

    [Fact]
    public void Capabilities_are_the_union_across_multiple_roles()
    {
        OnboardingPermissions.Has(["sales", "recruiter"], OnboardingPermissions.Create).Should().BeTrue();
        OnboardingPermissions.Has(["recruiter", "supervisor"], OnboardingPermissions.Complete).Should().BeTrue();
        OnboardingPermissions.Has(["sales", "recruiter"], OnboardingPermissions.Complete).Should().BeFalse();
    }

    [Fact]
    public void Unknown_role_grants_nothing()
    {
        OnboardingPermissions.Has(["janitor"], OnboardingPermissions.View).Should().BeFalse();
        OnboardingPermissions.ListFor(["janitor"]).Should().BeEmpty();
    }

    [Fact]
    public void No_roles_assigned_keeps_unscoped_full_access()
    {
        OnboardingPermissions.Has(Array.Empty<string>(), OnboardingPermissions.ManageTemplates).Should().BeTrue();
        OnboardingPermissions.Has(["", "  "], OnboardingPermissions.View).Should().BeTrue();
        OnboardingPermissions.ListFor(Array.Empty<string>()).Should().BeEquivalentTo(AllCapabilities);
    }

    [Fact]
    public void Sales_only_users_are_blocked_from_onboarding_routes_by_middleware_allowlist()
    {
        SalesAccessMiddleware.IsSalesReadOnly(["sales"]).Should().BeTrue();

        // Onboarding endpoints are intentionally absent from the sales allowlist (see SalesAccessMiddleware).
        var source = File.ReadAllText(FindRepoFile("Security", "SalesAccessMiddleware.cs"));
        var allowlists = source[source.IndexOf("ReadPrefixes", StringComparison.Ordinal)..source.IndexOf("private readonly RequestDelegate", StringComparison.Ordinal)];
        allowlists.Should().NotContain("/api/onboarding");
        allowlists.Should().NotContain("/api/onboardings");
    }

    private static string FindRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate " + string.Join('/', parts));
    }
}
