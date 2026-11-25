using FluentAssertions;
using nexthire_api.DTOs.CoreHr;
using nexthire_api.Helpers;
using nexthire_api.Services;
using Xunit;

namespace nexthire_api.Tests;

public class EmployeeValidationTests
{
    [Theory]
    [InlineData("Pending")]
    [InlineData("Onboarding")]
    [InlineData("Active")]
    [InlineData("OnLeave")]
    [InlineData("Inactive")]
    [InlineData("Terminated")]
    [InlineData("active")]
    [InlineData(" onleave ")]
    public void Valid_statuses_are_accepted(string status)
    {
        EmployeeValidation.IsValidStatus(status).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Fired")]
    [InlineData("On Leave")]
    public void Invalid_statuses_are_rejected(string? status)
    {
        EmployeeValidation.IsValidStatus(status).Should().BeFalse();
        EmployeeValidation.NormalizeStatus(status).Should().BeNull();
    }

    [Fact]
    public void NormalizeStatus_returns_canonical_casing()
    {
        EmployeeValidation.NormalizeStatus("onleave").Should().Be("OnLeave");
        EmployeeValidation.NormalizeStatus(" TERMINATED ").Should().Be("Terminated");
    }

    [Fact]
    public void Manager_cannot_be_the_employee_themself()
    {
        var id = Guid.NewGuid();
        EmployeeValidation.WouldCreateManagerCycle(id, id, _ => null).Should().BeTrue();
    }

    [Fact]
    public void Manager_cycle_detected_when_employee_is_in_proposed_managers_chain()
    {
        // ceo <- vp <- lead <- ic ; making ceo report to ic would close the loop.
        var ceo = Guid.NewGuid();
        var vp = Guid.NewGuid();
        var lead = Guid.NewGuid();
        var ic = Guid.NewGuid();
        var managers = new Dictionary<Guid, Guid?> { [ceo] = null, [vp] = ceo, [lead] = vp, [ic] = lead };

        EmployeeValidation.WouldCreateManagerCycle(ceo, ic, id => managers[id]).Should().BeTrue();
        EmployeeValidation.WouldCreateManagerCycle(vp, ic, id => managers[id]).Should().BeTrue();
    }

    [Fact]
    public void Manager_cycle_not_reported_for_valid_assignments()
    {
        var ceo = Guid.NewGuid();
        var vp = Guid.NewGuid();
        var lead = Guid.NewGuid();
        var newHire = Guid.NewGuid();
        var managers = new Dictionary<Guid, Guid?> { [ceo] = null, [vp] = ceo, [lead] = vp, [newHire] = null };

        EmployeeValidation.WouldCreateManagerCycle(newHire, lead, id => managers[id]).Should().BeFalse();
        EmployeeValidation.WouldCreateManagerCycle(lead, ceo, id => managers[id]).Should().BeFalse();
    }

    [Fact]
    public void Existing_cycle_elsewhere_does_not_hang_the_walk()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var other = Guid.NewGuid();
        var managers = new Dictionary<Guid, Guid?> { [a] = b, [b] = a };

        EmployeeValidation.WouldCreateManagerCycle(other, a, id => managers[id]).Should().BeFalse();
    }

    [Theory]
    [InlineData("Hired", null, true)]
    [InlineData("hired", "Open", true)]
    [InlineData("Interview", "hired", true)]
    [InlineData(" HIRED ", null, true)]
    [InlineData("Interview", "Open", false)]
    [InlineData(null, null, false)]
    public void Hired_is_detected_from_stage_name_or_status(string? stage, string? status, bool expected)
    {
        EmployeeValidation.IsHired(stage, status).Should().Be(expected);
    }

    [Fact]
    public void Valid_request_has_no_errors()
    {
        var request = new CreateEmployeeRequest
        {
            FirstName = "Ana",
            LastName = "Lopez",
            WorkEmail = "ana.lopez@example.com",
            EmploymentStatus = "Active",
            WorkModality = "hybrid"
        };

        EmployeeValidation.ValidateEmployee(request).Should().BeEmpty();
    }

    [Fact]
    public void Missing_names_bad_status_and_bad_email_are_reported()
    {
        var request = new CreateEmployeeRequest
        {
            FirstName = " ",
            LastName = "",
            WorkEmail = "not-an-email",
            EmploymentStatus = "Retired",
            WorkModality = "Moon"
        };

        var errors = EmployeeValidation.ValidateEmployee(request);

        errors.Should().Contain(e => e.Contains("First name"));
        errors.Should().Contain(e => e.Contains("Last name"));
        errors.Should().Contain(e => e.Contains("Employment status"));
        errors.Should().Contain(e => e.Contains("Work email"));
        errors.Should().Contain(e => e.Contains("Work modality"));
    }

    [Fact]
    public void Contract_end_before_start_is_rejected()
    {
        var request = new CreateEmployeeRequest
        {
            FirstName = "Ana",
            LastName = "Lopez",
            ContractStartDate = new DateTime(2026, 6, 1),
            ContractEndDate = new DateTime(2026, 5, 1)
        };

        EmployeeValidation.ValidateEmployee(request).Should().Contain(e => e.Contains("Contract end date"));
    }

    [Fact]
    public void Emergency_contact_requires_a_name()
    {
        var request = new CreateEmployeeRequest
        {
            FirstName = "Ana",
            LastName = "Lopez",
            EmergencyContacts = [new EmergencyContactDto { FullName = "" }]
        };

        EmployeeValidation.ValidateEmployee(request).Should().Contain(e => e.Contains("emergency contact"));
    }

    [Fact]
    public void Csv_parser_handles_quotes_commas_bom_and_blank_lines()
    {
        var csv = "\uFEFFFirstName,LastName,Department\r\n\"Ana\",\"Lopez, Jr.\",Sales\r\n\r\nLuis,\"Say \"\"hi\"\"\",IT\n";

        var rows = EmployeeCsv.Parse(csv);

        rows.Should().HaveCount(3);
        rows[0].Should().Equal("FirstName", "LastName", "Department");
        rows[1].Should().Equal("Ana", "Lopez, Jr.", "Sales");
        rows[2].Should().Equal("Luis", "Say \"hi\"", "IT");
    }

    [Fact]
    public void Csv_escape_quotes_values_and_neutralizes_formulas()
    {
        EmployeeCsv.Escape("a,b").Should().Be("\"a,b\"");
        EmployeeCsv.Escape("say \"x\"").Should().Be("\"say \"\"x\"\"\"");
        EmployeeCsv.Escape("=SUM(A1)").Should().Be("'=SUM(A1)");
        EmployeeCsv.Escape(null).Should().BeEmpty();
    }

    [Fact]
    public void Csv_header_normalization_ignores_case_and_separators()
    {
        EmployeeCsv.NormalizeHeader("First Name").Should().Be("firstname");
        EmployeeCsv.NormalizeHeader("employment_status").Should().Be("employmentstatus");
    }
}
