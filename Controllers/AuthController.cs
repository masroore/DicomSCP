using Microsoft.AspNetCore.Mvc;
using DicomSCP.Data;

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
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var isValid = await _repository.ValidateUserAsync(request.Username, request.Password);
        if (!isValid)
        {
            return Unauthorized("用户名或密码错误");
        }

        Response.Cookies.Append("username", request.Username, new CookieOptions
        {
            HttpOnly = true,  // 防止 JS 访问
            Expires = DateTime.Now.AddMinutes(30),
            SameSite = SameSiteMode.Strict,
            IsEssential = true,
            Path = "/",
            Secure = true,
        });

        return Ok();
    }

    [HttpPost("logout")]
    public IActionResult Logout()
    {
        Response.Cookies.Delete("username", new CookieOptions
        {
            Path = "/"
        });
        return Ok();
    }
}

public class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
} 