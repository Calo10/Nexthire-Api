using FluentAssertions;
using nexthire_api.DTOs.CoreHr;
using nexthire_api.Security;
using nexthire_api.Services;
using Xunit;

namespace nexthire_api.Tests;

public class HrDocumentValidationTests
{
    private static readonly DateTime Today = new(2026, 11, 25);

    [Theory]
    [InlineData("contract", "Contract")]
    [InlineData(" Identification ", "Identification")]
    [InlineData("ID", "Identification")]
    [InlineData("certificates", "Certification")]
    [InlineData("TAX", "Tax")]
    [InlineData("other", "Other")]
    public void NormalizeCategory_maps_known_values_and_aliases(string input, string expected) =>
        HrDocumentValidation.NormalizeCategory(input).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Payroll")]
    public void NormalizeCategory_returns_null_for_blank_or_unknown(string? input) =>
        HrDocumentValidation.NormalizeCategory(input).Should().BeNull();

    [Fact]
    public void NormalizeCategoryForDisplay_rolls_legacy_values_into_Other() =>
        HrDocumentValidation.NormalizeCategoryForDisplay("Payroll").Should().Be(HrDocumentCategories.Other);

    [Fact]
    public void KnownNonOtherStoredValues_excludes_Other_and_includes_aliases()
    {
        var known = HrDocumentValidation.KnownNonOtherStoredValues();
        known.Should().NotContain(HrDocumentCategories.Other);
        known.Should().Contain(new[] { "Contract", "Contracts", "Identification", "ID", "Tax" });
    }

    [Theory]
    [InlineData("Active", null, "Active")]
    [InlineData("Active", "2026-12-25", "Expiring")] // exactly 30 days out
    [InlineData("Active", "2026-12-26", "Active")] // 31 days out
    [InlineData("Active", "2026-11-25", "Expiring")] // expires today
    [InlineData("Active", "2026-11-24", "Expired")]
    [InlineData("Expired", null, "Expired")]
    [InlineData("Pending", "2026-01-01", "Pending")]
    [InlineData("Archived", null, "Archived")]
    public void ComputeDisplayStatus_follows_expiry_rules(string stored, string? expires, string expected)
    {
        DateTime? expiresAt = expires == null ? null : DateTime.Parse(expires);
        HrDocumentValidation.ComputeDisplayStatus(stored, expiresAt, Today).Should().Be(expected);
    }

    [Theory]
    [InlineData("valid", "Active")]
    [InlineData("expiring", "Expiring")]
    [InlineData("EXPIRED", "Expired")]
    [InlineData("pending", "Pending")]
    public void NormalizeDisplayStatus_accepts_case_insensitive_values(string input, string expected) =>
        HrDocumentValidation.NormalizeDisplayStatus(input).Should().Be(expected);

    [Fact]
    public void NormalizeDisplayStatus_rejects_unknown_status() =>
        HrDocumentValidation.NormalizeDisplayStatus("Archived").Should().BeNull();

    [Fact]
    public void ComputeRequestStatus_marks_past_due_open_requests_overdue()
    {
        HrDocumentValidation.ComputeRequestStatus("Open", Today.AddDays(-1), Today).Should().Be("Overdue");
        HrDocumentValidation.ComputeRequestStatus("Open", Today, Today).Should().Be("Open");
        HrDocumentValidation.ComputeRequestStatus("Open", null, Today).Should().Be("Open");
        HrDocumentValidation.ComputeRequestStatus("Fulfilled", Today.AddDays(-10), Today).Should().Be("Fulfilled");
    }

    [Fact]
    public void ValidateCreate_requires_employee_category_name_and_a_source()
    {
        var errors = HrDocumentValidation.ValidateCreate(new CreateHrDocumentRequest());

        errors.Should().Contain(e => e.Contains("employeeId"));
        errors.Should().Contain(e => e.Contains("Category is required"));
        errors.Should().Contain(e => e.Contains("Name is required"));
        errors.Should().Contain(e => e.Contains("documentId or an externalUrl"));
    }

    [Fact]
    public void ValidateCreate_accepts_link_only_document()
    {
        var errors = HrDocumentValidation.ValidateCreate(new CreateHrDocumentRequest
        {
            EmployeeId = Guid.NewGuid(),
            Category = "contract",
            Name = "Employment agreement",
            ExternalUrl = "https://example.com/agreement.pdf"
        });

        errors.Should().BeEmpty();
    }

    [Fact]
    public void ValidateMetadata_rejects_bad_category_status_and_url()
    {
        var errors = HrDocumentValidation.ValidateMetadata("Payroll", "Doc", "Archived", "ftp://example.com/x");

        errors.Should().Contain(e => e.StartsWith("Category must be one of"));
        errors.Should().Contain(e => e.StartsWith("Status must be"));
        errors.Should().Contain(e => e.Contains("http(s)"));
    }

    [Fact]
    public void ValidateDocumentRequest_checks_required_fields_and_lengths()
    {
        HrDocumentValidation.ValidateDocumentRequest(new CreateDocumentRequest()).Should().HaveCount(3);

        HrDocumentValidation.ValidateDocumentRequest(new CreateDocumentRequest
        {
            EmployeeId = Guid.NewGuid(),
            Category = "Medical",
            Title = "Vaccination record"
        }).Should().BeEmpty();

        HrDocumentValidation.ValidateDocumentRequest(new CreateDocumentRequest
        {
            EmployeeId = Guid.NewGuid(),
            Category = "Medical",
            Title = new string('x', 301),
            Notes = new string('y', 1001)
        }).Should().HaveCount(2);
    }

    [Theory]
    [InlineData("passport-scan.pdf", "passport-scan")]
    [InlineData("", "Document")]
    [InlineData(null, "Document")]
    public void DefaultNameFromFileName_strips_extension(string? fileName, string expected) =>
        HrDocumentValidation.DefaultNameFromFileName(fileName).Should().Be(expected);

    [Fact]
    public void Documents_hub_permissions_follow_role_capabilities()
    {
        // Reads need Employees.View, writes/uploads need Employees.ManageDocuments.
        HrPermissions.Has(["recruiter"], HrPermissions.View).Should().BeTrue();
        HrPermissions.Has(["recruiter"], HrPermissions.ManageDocuments).Should().BeTrue();
        HrPermissions.Has(["account_admin"], HrPermissions.ManageDocuments).Should().BeTrue();
        HrPermissions.Has(["sales"], HrPermissions.View).Should().BeFalse();
        HrPermissions.Has(["sales"], HrPermissions.ManageDocuments).Should().BeFalse();
    }
}
