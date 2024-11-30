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

        Response.Cookies.Append("auth", "true", new CookieOptions
        {
            HttpOnly = true,
            Expires = DateTime.Now.AddMinutes(30),
            SameSite = SameSiteMode.Strict,
            IsEssential = true,
            Path = "/",
            Secure = true,
        });

        Response.Cookies.Append("username", request.Username, new CookieOptions
        {
            HttpOnly = false,
            Expires = DateTime.Now.AddMinutes(30),
            SameSite = SameSiteMode.Strict,
            IsEssential = true,
            Path = "/",
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

    // 添加修改密码的请求模型
    public class ChangePasswordRequest
    {
        public string OldPassword { get; set; } = string.Empty;
        public string NewPassword { get; set; } = string.Empty;
    }

    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        var username = Request.Cookies["username"];
        if (string.IsNullOrEmpty(username))
        {
            return Unauthorized("未登录");
        }

        // 验证旧密码
        var isValid = await _repository.ValidateUserAsync(username, request.OldPassword);
        if (!isValid)
        {
            return BadRequest("旧密码错误");
        }

        // 修改密码
        await _repository.ChangePasswordAsync(username, request.NewPassword);

        // 清除登录状态
        Response.Cookies.Delete("username");
        
        return Ok();
    }
}

public class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
} 