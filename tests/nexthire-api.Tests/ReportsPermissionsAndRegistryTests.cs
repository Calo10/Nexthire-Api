using nexthire_api.Security;
using nexthire_api.Services.Reporting;
using Xunit;

namespace nexthire_api.Tests;

public class ReportsPermissionsAndRegistryTests
{
    [Fact]
    public void Permissions_exclude_sales()
    {
        Assert.False(ReportsPermissions.Has(["sales"], ReportsPermissions.View));
        Assert.Empty(ReportsPermissions.ListFor(["sales"]));
    }

    [Fact]
    public void Permissions_recruiter_cannot_view_compensation()
    {
        Assert.True(ReportsPermissions.Has(["recruiter"], ReportsPermissions.ViewWorkforce));
        Assert.False(ReportsPermissions.Has(["recruiter"], ReportsPermissions.ViewCompensation));
    }

    [Fact]
    public void Registry_has_no_recruitment_datasets()
    {
        var keys = ReportDatasetRegistry.GetAll().Select(d => d.Key).ToList();
        Assert.Contains("employees", keys);
        Assert.Contains("onboardings", keys);
        Assert.Contains("offboardings", keys);
        Assert.Contains("employee_documents", keys);
        Assert.Contains("organizational_positions", keys);
        Assert.DoesNotContain(keys, k => k.Contains("candidate", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(keys, k => k.Contains("application", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(keys, k => k.Contains("job", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(keys, k => k.Contains("pipeline", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(keys, k => k.Contains("recruit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Registry_rejects_unknown_fields()
    {
        var dataset = ReportDatasetRegistry.Get("employees");
        Assert.NotNull(dataset);
        var ok = ReportDatasetRegistry.ValidateConfiguration(
            dataset!,
            """{"dimensions":["department"],"measures":[{"field":"id","agg":"count"}],"limit":100}""");
        Assert.True(ok.IsValid, string.Join("; ", ok.Errors));

        var bad = ReportDatasetRegistry.ValidateConfiguration(
            dataset!,
            """{"dimensions":["not_a_real_field"],"measures":[{"field":"id","agg":"count"}]}""");
        Assert.False(bad.IsValid);
        Assert.NotEmpty(bad.Errors);
    }

    [Fact]
    public void Registry_accepts_string_measures_as_count()
    {
        var dataset = ReportDatasetRegistry.Get("employees");
        Assert.NotNull(dataset);
        var result = ReportDatasetRegistry.ValidateConfiguration(
            dataset!,
            """{"dimensions":["department"],"measures":["id"],"limit":50}""");
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }
}
