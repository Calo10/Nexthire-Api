using System.Data.SqlClient;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using nexthire_api.DTOs;
using nexthire_api.Repositories;

namespace nexthire_api.Controllers;

[ApiController]
[Route("api/dashboard")]
[Authorize]
public class DashboardController : ControllerBase
{
    private readonly IDashboardRepository _dashboardRepository;
    private readonly ILogger<DashboardController> _logger;
    private readonly IHostEnvironment _env;
    private readonly IConfiguration _configuration;

    public DashboardController(
        IDashboardRepository dashboardRepository,
        ILogger<DashboardController> logger,
        IHostEnvironment env,
        IConfiguration configuration)
    {
        _dashboardRepository = dashboardRepository;
        _logger = logger;
        _env = env;
        _configuration = configuration;
    }

    /// <summary>
    /// Get dashboard summary with KPIs
    /// </summary>
    [HttpGet("summary")]
    public async Task<ActionResult<DashboardSummaryDto>> GetSummary(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] Guid? jobId)
    {
        try
        {
            if (!TryGetOrgId(out var orgId, out var unauthorized))
                return unauthorized!;

            var summary = await _dashboardRepository.GetSummaryAsync(orgId, from, to, jobId);
            return Ok(summary);
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "Dashboard DB connection failed (summary)");

            if (_env.IsDevelopment() && _configuration.GetValue("Dashboard:DevFallback", false))
            {
                return Ok(new DashboardSummaryDto
                {
                    OpenJobs = 0,
                    NewCandidates = 0,
                    NewApplications = 0,
                    UpcomingInterviews = 0,
                    OffersSent = 0,
                    AvgTimeToHireDays = null
                });
            }

            return Problem(
                title: "Database connection failed",
                detail: "Dashboard cannot connect to SQL Server. Check ConnectionStrings:NextHireDb and ensure SQL Server is running/reachable (e.g., localhost:1433).",
                statusCode: 500);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting dashboard summary");
            return StatusCode(500, new { message = "An error occurred while retrieving dashboard summary" });
        }
    }

    /// <summary>
    /// Get applications count by stage
    /// </summary>
    [HttpGet("applications-by-stage")]
    public async Task<ActionResult<IEnumerable<ApplicationsByStageDto>>> GetApplicationsByStage(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] Guid? jobId)
    {
        try
        {
            if (!TryGetOrgId(out var orgId, out var unauthorized))
                return unauthorized!;

            var result = await _dashboardRepository.GetApplicationsByStageAsync(orgId, from, to, jobId);
            return Ok(result);
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "Dashboard DB connection failed (applications-by-stage)");

            if (_env.IsDevelopment() && _configuration.GetValue("Dashboard:DevFallback", false))
            {
                return Ok(Array.Empty<ApplicationsByStageDto>());
            }

            return Problem(
                title: "Database connection failed",
                detail: "Dashboard cannot connect to SQL Server. Check ConnectionStrings:NextHireDb and ensure SQL Server is running/reachable (e.g., localhost:1433).",
                statusCode: 500);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting applications by stage");
            return StatusCode(500, new { message = "An error occurred while retrieving applications by stage" });
        }
    }

    /// <summary>
    /// Get activity trend by day
    /// </summary>
    [HttpGet("activity-trend")]
    public async Task<ActionResult<IEnumerable<ActivityTrendDto>>> GetActivityTrend(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] Guid? jobId)
    {
        try
        {
            if (!TryGetOrgId(out var orgId, out var unauthorized))
                return unauthorized!;

            var result = await _dashboardRepository.GetActivityTrendAsync(orgId, from, to, jobId);
            return Ok(result);
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "Dashboard DB connection failed (activity-trend)");

            if (_env.IsDevelopment() && _configuration.GetValue("Dashboard:DevFallback", false))
            {
                return Ok(Array.Empty<ActivityTrendDto>());
            }

            return Problem(
                title: "Database connection failed",
                detail: "Dashboard cannot connect to SQL Server. Check ConnectionStrings:NextHireDb and ensure SQL Server is running/reachable (e.g., localhost:1433).",
                statusCode: 500);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting activity trend");
            return StatusCode(500, new { message = "An error occurred while retrieving activity trend" });
        }
    }

    /// <summary>
    /// Get top 20 jobs with application counts
    /// </summary>
    [HttpGet("my-jobs")]
    public async Task<ActionResult<IEnumerable<MyJobDto>>> GetMyJobs(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to)
    {
        try
        {
            if (!TryGetOrgId(out var orgId, out var unauthorized))
                return unauthorized!;

            var result = await _dashboardRepository.GetMyJobsAsync(orgId, from, to);
            return Ok(result);
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "Dashboard DB connection failed (my-jobs)");

            if (_env.IsDevelopment() && _configuration.GetValue("Dashboard:DevFallback", false))
            {
                return Ok(Array.Empty<MyJobDto>());
            }

            return Problem(
                title: "Database connection failed",
                detail: "Dashboard cannot connect to SQL Server. Check ConnectionStrings:NextHireDb and ensure SQL Server is running/reachable (e.g., localhost:1433).",
                statusCode: 500);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting my jobs");
            return StatusCode(500, new { message = "An error occurred while retrieving jobs" });
        }
    }

    /// <summary>
    /// Get recent candidates with current stage
    /// </summary>
    [HttpGet("recent-candidates")]
    public async Task<ActionResult<IEnumerable<RecentCandidateDto>>> GetRecentCandidates(
        [FromQuery] int limit = 10)
    {
        try
        {
            if (!TryGetOrgId(out var orgId, out var unauthorized))
                return unauthorized!;

            var result = await _dashboardRepository.GetRecentCandidatesAsync(orgId, limit);
            return Ok(result);
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "Dashboard DB connection failed (recent-candidates)");

            if (_env.IsDevelopment() && _configuration.GetValue("Dashboard:DevFallback", false))
            {
                return Ok(Array.Empty<RecentCandidateDto>());
            }

            return Problem(
                title: "Database connection failed",
                detail: "Dashboard cannot connect to SQL Server. Check ConnectionStrings:NextHireDb and ensure SQL Server is running/reachable (e.g., localhost:1433).",
                statusCode: 500);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting recent candidates");
            return StatusCode(500, new { message = "An error occurred while retrieving recent candidates" });
        }
    }

    /// <summary>
    /// Get upcoming tasks ordered by due date
    /// </summary>
    [HttpGet("upcoming-tasks")]
    public async Task<ActionResult<IEnumerable<UpcomingTaskDto>>> GetUpcomingTasks(
        [FromQuery] int limit = 10)
    {
        try
        {
            if (!TryGetOrgId(out var orgId, out var unauthorized))
                return unauthorized!;

            var result = await _dashboardRepository.GetUpcomingTasksAsync(orgId, limit);
            return Ok(result);
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "Dashboard DB connection failed (upcoming-tasks)");

            if (_env.IsDevelopment() && _configuration.GetValue("Dashboard:DevFallback", false))
            {
                return Ok(Array.Empty<UpcomingTaskDto>());
            }

            return Problem(
                title: "Database connection failed",
                detail: "Dashboard cannot connect to SQL Server. Check ConnectionStrings:NextHireDb and ensure SQL Server is running/reachable (e.g., localhost:1433).",
                statusCode: 500);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting upcoming tasks");
            return StatusCode(500, new { message = "An error occurred while retrieving upcoming tasks" });
        }
    }

    private bool TryGetOrgId(out Guid orgId, out ActionResult? unauthorized)
    {
        unauthorized = null;
        orgId = Guid.Empty;

        var orgClaim = User.FindFirst("nexa_org_id")?.Value;
        if (string.IsNullOrWhiteSpace(orgClaim) || !Guid.TryParse(orgClaim, out orgId))
        {
            unauthorized = Unauthorized(new { message = "Missing org context. Login again." });
            return false;
        }

        return true;
    }
}
