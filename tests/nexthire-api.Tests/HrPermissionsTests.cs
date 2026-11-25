using FluentAssertions;
using nexthire_api.Security;
using Xunit;

namespace nexthire_api.Tests;

public class HrPermissionsTests
{
    private static readonly string[] AllCapabilities =
    [
        HrPermissions.View, HrPermissions.Create, HrPermissions.Edit, HrPermissions.Archive,
        HrPermissions.Import, HrPermissions.Export, HrPermissions.ViewSensitiveData,
        HrPermissions.ManageDocuments, HrPermissions.ViewCompensation, HrPermissions.ManageCompensation
    ];

    [Theory]
    [InlineData("account_admin")]
    [InlineData("supervisor")]
    public void Admin_and_supervisor_have_every_capability(string role)
    {
        foreach (var capability in AllCapabilities)
            HrPermissions.Has([role], capability).Should().BeTrue($"{role} should have {capability}");
    }

    [Fact]
    public void Recruiter_can_manage_employees_but_not_sensitive_or_compensation_or_archive()
    {
        string[] roles = ["recruiter"];

        HrPermissions.Has(roles, HrPermissions.View).Should().BeTrue();
        HrPermissions.Has(roles, HrPermissions.Create).Should().BeTrue();
        HrPermissions.Has(roles, HrPermissions.Edit).Should().BeTrue();
        HrPermissions.Has(roles, HrPermissions.Export).Should().BeTrue();
        HrPermissions.Has(roles, HrPermissions.ManageDocuments).Should().BeTrue();

        HrPermissions.Has(roles, HrPermissions.Archive).Should().BeFalse();
        HrPermissions.Has(roles, HrPermissions.Import).Should().BeFalse();
        HrPermissions.Has(roles, HrPermissions.ViewSensitiveData).Should().BeFalse();
        HrPermissions.Has(roles, HrPermissions.ViewCompensation).Should().BeFalse();
        HrPermissions.Has(roles, HrPermissions.ManageCompensation).Should().BeFalse();
    }

    [Fact]
    public void Sales_has_no_hr_capabilities()
    {
        foreach (var capability in AllCapabilities)
            HrPermissions.Has(["sales"], capability).Should().BeFalse($"sales must not have {capability}");

        HrPermissions.ListFor(["sales"]).Should().BeEmpty();
    }

    [Fact]
    public void Role_codes_are_case_insensitive_and_trimmed()
    {
        HrPermissions.Has(["  Account_Admin "], HrPermissions.ViewCompensation).Should().BeTrue();
        HrPermissions.Has(["RECRUITER"], HrPermissions.Create).Should().BeTrue();
    }

    [Fact]
    public void Capabilities_are_the_union_across_multiple_roles()
    {
        HrPermissions.Has(["sales", "recruiter"], HrPermissions.Create).Should().BeTrue();
        HrPermissions.Has(["recruiter", "supervisor"], HrPermissions.ViewCompensation).Should().BeTrue();
        HrPermissions.Has(["sales", "recruiter"], HrPermissions.ViewCompensation).Should().BeFalse();
    }

    [Fact]
    public void Unknown_role_grants_nothing()
    {
        HrPermissions.Has(["janitor"], HrPermissions.View).Should().BeFalse();
    }

    [Fact]
    public void No_roles_assigned_keeps_unscoped_full_access()
    {
        HrPermissions.Has(Array.Empty<string>(), HrPermissions.ViewCompensation).Should().BeTrue();
        HrPermissions.Has(["", "  "], HrPermissions.View).Should().BeTrue();
        HrPermissions.ListFor(Array.Empty<string>()).Should().BeEquivalentTo(AllCapabilities);
    }

    [Fact]
    public void ListFor_returns_distinct_capabilities_for_role()
    {
        var caps = HrPermissions.ListFor(["recruiter"]);

        caps.Should().BeEquivalentTo(
        [
            HrPermissions.View, HrPermissions.Create, HrPermissions.Edit,
            HrPermissions.Export, HrPermissions.ManageDocuments
        ]);
    }

    [Fact]
    public void Sales_only_users_are_blocked_from_hr_routes_by_middleware_allowlist()
    {
        SalesAccessMiddleware.IsSalesReadOnly(["sales"]).Should().BeTrue();

        // HR endpoints are intentionally absent from the sales allowlist (see SalesAccessMiddleware).
        var source = File.ReadAllText(FindRepoFile("Security", "SalesAccessMiddleware.cs"));
        source.Should().NotContain("/api/employees");
        source.Should().NotContain("/api/hr");
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
