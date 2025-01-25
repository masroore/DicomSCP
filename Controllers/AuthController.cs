using Microsoft.AspNetCore.Mvc;
using DicomSCP.Data;
using DicomSCP.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;

namespace DicomSCP.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly DicomRepository _repository;

    public AuthController(DicomRepository repository)
    {
        _repository = repository;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var isValid = await _repository.ValidateUserAsync(request.Username, request.Password);
        if (!isValid)
        {
            return Unauthorized("Invalid username or password");
        }

        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.Name, request.Username)
        };

        await HttpContext.SignInAsync(
            "CustomAuth",
            new ClaimsPrincipal(new ClaimsIdentity(claims, "CustomAuth")),
            new AuthenticationProperties
            {
                IsPersistent = true,
                IssuedUtc = DateTimeOffset.UtcNow,
                AllowRefresh = true
            });

        return Ok();
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        try
        {
            var username = User.Identity?.Name ?? "unknown";
            await HttpContext.SignOutAsync("CustomAuth");

            DicomLogger.Information("Api", "[API] User logged out - Username: {Username}", username);
            return Ok();
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "[API] Logout exception");
            return StatusCode(500, "Logout failed");
        }
    }

    [Authorize]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        try
        {
            var username = User.Identity?.Name;
            if (string.IsNullOrEmpty(username))
            {
                DicomLogger.Warning("Api", "[API] Password change failed - Reason: Not logged in");
                return Unauthorized("Not logged in");
            }

            // Validate old password
            var isValid = await _repository.ValidateUserAsync(username, request.OldPassword);
            if (!isValid)
            {
                DicomLogger.Warning("Api", "[API] Password change failed - Username: {Username}, Reason: Incorrect old password", username);
                return BadRequest("Incorrect old password");
            }

            // Change password
            await _repository.ChangePasswordAsync(username, request.NewPassword);

            // Clear login status
            await HttpContext.SignOutAsync("CustomAuth");

            DicomLogger.Information("Api", "[API] Password changed successfully - Username: {Username}", username);
            return Ok();
        }
        catch (Exception ex)
        {
            var username = User.Identity?.Name ?? "unknown";
            DicomLogger.Error("Api", ex, "[API] Password change exception - Username: {Username}", username);
            return StatusCode(500, "Password change failed");
        }
    }

    [Authorize]
    [HttpGet("check-session")]
    public IActionResult CheckSession()
    {
        // Since the middleware has already handled authentication, just return user information here
        return Ok(new { username = User.Identity?.Name });
    }
}

public class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class ChangePasswordRequest
{
    public string OldPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}
