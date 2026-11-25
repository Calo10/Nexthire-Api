using nexthire_api.Security;
using nexthire_api.Services;
using Xunit;

namespace nexthire_api.Tests;

public class OrganizationValidationTests
{
    [Theory]
    [InlineData("Active", "Active")]
    [InlineData("planned", "Planned")]
    [InlineData("FROZEN", "Frozen")]
    [InlineData("Archived", "Archived")]
    [InlineData("nope", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizePositionStatus_maps_known_values(string? input, string? expected)
    {
        Assert.Equal(expected, OrganizationValidation.NormalizePositionStatus(input));
    }

    [Theory]
    [InlineData("Office", "Office")]
    [InlineData("remote", "Remote")]
    [InlineData("Hybrid", "Hybrid")]
    [InlineData("ClientSite", "ClientSite")]
    [InlineData("warehouse", null)]
    public void NormalizeLocationType_maps_known_values(string? input, string? expected)
    {
        Assert.Equal(expected, OrganizationValidation.NormalizeLocationType(input));
    }

    [Theory]
    [InlineData("operations", "operations")]
    [InlineData("Technology", "technology")]
    [InlineData("weird", "default")]
    [InlineData(null, "default")]
    public void NormalizeIcon_falls_back_to_default(string? input, string expected)
    {
        Assert.Equal(expected, OrganizationValidation.NormalizeIcon(input));
    }

    [Theory]
    [InlineData("#6366F1", "#6366F1")]
    [InlineData("#abc", "#ABC")]
    [InlineData("red", null)]
    [InlineData("", null)]
    public void NormalizeColor_accepts_hex_only(string? input, string? expected)
    {
        Assert.Equal(expected, OrganizationValidation.NormalizeColor(input));
    }

    [Fact]
    public void Permissions_account_admin_has_full_set()
    {
        var caps = OrganizationPermissions.ListFor(["account_admin"]);
        Assert.Contains(OrganizationPermissions.View, caps);
        Assert.Contains(OrganizationPermissions.ManageDepartments, caps);
        Assert.Contains(OrganizationPermissions.ManagePositions, caps);
        Assert.Contains(OrganizationPermissions.AssignEmployees, caps);
        Assert.True(OrganizationPermissions.Has(["account_admin"], OrganizationPermissions.Export));
    }

    [Fact]
    public void Permissions_recruiter_is_read_mostly()
    {
        Assert.True(OrganizationPermissions.Has(["recruiter"], OrganizationPermissions.View));
        Assert.True(OrganizationPermissions.Has(["recruiter"], OrganizationPermissions.ViewPeople));
        Assert.False(OrganizationPermissions.Has(["recruiter"], OrganizationPermissions.ManageDepartments));
        Assert.False(OrganizationPermissions.Has(["recruiter"], OrganizationPermissions.AssignEmployees));
    }

    [Fact]
    public void Permissions_sales_has_none()
    {
        Assert.False(OrganizationPermissions.Has(["sales"], OrganizationPermissions.View));
        Assert.Empty(OrganizationPermissions.ListFor(["sales"]));
    }
}
