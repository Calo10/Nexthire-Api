using Microsoft.AspNetCore.Mvc;
using nexthire_api.DTOs;
using nexthire_api.Services;

namespace nexthire_api.Controllers;

[ApiController]
[Route("api/v1/email")]
public class EmailController : ControllerBase
{
    private readonly IEmailService _emailService;
    private readonly ILogger<EmailController> _logger;

    public EmailController(IEmailService emailService, ILogger<EmailController> logger)
    {
        _emailService = emailService;
        _logger = logger;
    }

    [HttpPost("send")]
    public async Task<IActionResult> Send([FromBody] SendEmailRequestDto request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            await _emailService.SendEmailAsync(
                request.ToEmail,
                request.Subject,
                request.PlainText,
                request.Html,
                cancellationToken);

            return Ok(new { message = "Email sent successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email via /v1/email/send endpoint");
            return StatusCode(500, new { error = "Failed to send email" });
        }
    }
}
