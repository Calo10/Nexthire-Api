using FluentAssertions;
using nexthire_api.Security;
using Xunit;

namespace nexthire_api.Tests;

public class OffboardingPermissionsTests
{
    private static readonly string[] AllCapabilities =
    [
        OffboardingPermissions.View, OffboardingPermissions.Create, OffboardingPermissions.Edit,
        OffboardingPermissions.Cancel, OffboardingPermissions.Complete, OffboardingPermissions.ManageTemplates,
        OffboardingPermissions.ManageTasks, OffboardingPermissions.ApproveClearance,
        OffboardingPermissions.ManageAssets, OffboardingPermissions.ManageAccess,
        OffboardingPermissions.ManageDocuments, OffboardingPermissions.ManageExitInterviews,
        OffboardingPermissions.ViewConfidentialInformation, OffboardingPermissions.ManageSettlement,
        OffboardingPermissions.ManageAutomations
    ];

    [Fact]
    public void Capability_strings_use_the_Offboarding_prefix()
    {
        OffboardingPermissions.All.Should().BeEquivalentTo(AllCapabilities);
        OffboardingPermissions.All.Should().HaveCount(15);
        OffboardingPermissions.All.Should().OnlyContain(c => c.StartsWith("Offboarding."));
        OffboardingPermissions.View.Should().Be("Offboarding.View");
        OffboardingPermissions.ViewConfidentialInformation.Should().Be("Offboarding.ViewConfidentialInformation");
        OffboardingPermissions.ManageAutomations.Should().Be("Offboarding.ManageAutomations");
    }

    [Theory]
    [InlineData("account_admin")]
    [InlineData("supervisor")]
    public void Admin_and_supervisor_have_every_capability(string role)
    {
        foreach (var capability in AllCapabilities)
            OffboardingPermissions.Has([role], capability).Should().BeTrue($"{role} should have {capability}");
    }

    [Fact]
    public void Recruiter_can_view_create_edit_and_manage_tasks_only()
    {
        string[] roles = ["recruiter"];

        OffboardingPermissions.Has(roles, OffboardingPermissions.View).Should().BeTrue();
        OffboardingPermissions.Has(roles, OffboardingPermissions.Create).Should().BeTrue();
        OffboardingPermissions.Has(roles, OffboardingPermissions.Edit).Should().BeTrue();
        OffboardingPermissions.Has(roles, OffboardingPermissions.ManageTasks).Should().BeTrue();

        OffboardingPermissions.Has(roles, OffboardingPermissions.Cancel).Should().BeFalse();
        OffboardingPermissions.Has(roles, OffboardingPermissions.Complete).Should().BeFalse();
        OffboardingPermissions.Has(roles, OffboardingPermissions.ManageTemplates).Should().BeFalse();
        OffboardingPermissions.Has(roles, OffboardingPermissions.ApproveClearance).Should().BeFalse();
        OffboardingPermissions.Has(roles, OffboardingPermissions.ManageAssets).Should().BeFalse();
        OffboardingPermissions.Has(roles, OffboardingPermissions.ManageAccess).Should().BeFalse();
        OffboardingPermissions.Has(roles, OffboardingPermissions.ManageDocuments).Should().BeFalse();
        OffboardingPermissions.Has(roles, OffboardingPermissions.ManageExitInterviews).Should().BeFalse();
        OffboardingPermissions.Has(roles, OffboardingPermissions.ManageSettlement).Should().BeFalse();
        OffboardingPermissions.Has(roles, OffboardingPermissions.ManageAutomations).Should().BeFalse();

        OffboardingPermissions.ListFor(roles).Should().BeEquivalentTo(
        [
            OffboardingPermissions.View, OffboardingPermissions.Create,
            OffboardingPermissions.Edit, OffboardingPermissions.ManageTasks
        ]);
    }

    [Fact]
    public void Recruiter_cannot_view_confidential_information()
    {
        OffboardingPermissions.Has(["recruiter"], OffboardingPermissions.ViewConfidentialInformation).Should().BeFalse();
        OffboardingPermissions.ListFor(["recruiter"]).Should().NotContain(OffboardingPermissions.ViewConfidentialInformation);
        OffboardingPermissions.Has(["account_admin"], OffboardingPermissions.ViewConfidentialInformation).Should().BeTrue();
        OffboardingPermissions.Has(["supervisor"], OffboardingPermissions.ViewConfidentialInformation).Should().BeTrue();
    }

    [Fact]
    public void Sales_has_no_offboarding_capabilities()
    {
        foreach (var capability in AllCapabilities)
            OffboardingPermissions.Has(["sales"], capability).Should().BeFalse($"sales must not have {capability}");

        OffboardingPermissions.ListFor(["sales"]).Should().BeEmpty();
    }

    [Fact]
    public void Role_codes_are_case_insensitive_and_trimmed()
    {
        OffboardingPermissions.Has(["  Account_Admin "], OffboardingPermissions.ApproveClearance).Should().BeTrue();
        OffboardingPermissions.Has(["RECRUITER"], OffboardingPermissions.Create).Should().BeTrue();
    }

    [Fact]
    public void Capabilities_are_the_union_across_multiple_roles()
    {
        OffboardingPermissions.Has(["sales", "recruiter"], OffboardingPermissions.Create).Should().BeTrue();
        OffboardingPermissions.Has(["recruiter", "supervisor"], OffboardingPermissions.Complete).Should().BeTrue();
        OffboardingPermissions.Has(["sales", "recruiter"], OffboardingPermissions.Complete).Should().BeFalse();
        OffboardingPermissions.Has(["sales", "recruiter"], OffboardingPermissions.ViewConfidentialInformation).Should().BeFalse();
    }

    [Fact]
    public void Unknown_role_grants_nothing()
    {
        OffboardingPermissions.Has(["janitor"], OffboardingPermissions.View).Should().BeFalse();
        OffboardingPermissions.ListFor(["janitor"]).Should().BeEmpty();
    }

    [Fact]
    public void No_roles_assigned_keeps_unscoped_full_access()
    {
        OffboardingPermissions.Has(Array.Empty<string>(), OffboardingPermissions.ManageTemplates).Should().BeTrue();
        OffboardingPermissions.Has(["", "  "], OffboardingPermissions.View).Should().BeTrue();
        OffboardingPermissions.ListFor(Array.Empty<string>()).Should().BeEquivalentTo(AllCapabilities);
    }

    [Fact]
    public void Sales_only_users_are_blocked_from_offboarding_routes_by_middleware_allowlist()
    {
        SalesAccessMiddleware.IsSalesReadOnly(["sales"]).Should().BeTrue();

        // Offboarding endpoints are intentionally absent from the sales allowlist (see SalesAccessMiddleware).
        var source = File.ReadAllText(FindRepoFile("Security", "SalesAccessMiddleware.cs"));
        var allowlists = source[source.IndexOf("ReadPrefixes", StringComparison.Ordinal)..source.IndexOf("private readonly RequestDelegate", StringComparison.Ordinal)];
        allowlists.Should().NotContain("/api/offboarding");
        allowlists.Should().NotContain("/api/offboardings");
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
