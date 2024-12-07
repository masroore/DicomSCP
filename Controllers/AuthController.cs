using Microsoft.AspNetCore.Mvc;
using DicomSCP.Data;
using DicomSCP.Services;

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
        try
        {
            DicomLogger.Debug("Api", "[API] 尝试登录 - 用户名: {Username}", request.Username);

            var isValid = await _repository.ValidateUserAsync(request.Username, request.Password);
            if (!isValid)
            {
                DicomLogger.Warning("Api", "[API] 登录失败 - 用户名: {Username}, 原因: 用户名或密码错误", request.Username);
                return Unauthorized("用户名或密码错误");
            }
            
            // 生成包含时间戳的认证值
            var token = Guid.NewGuid().ToString("N");
            var authValue = $"{token}|{DateTime.Now:O}";
            
            Response.Cookies.Append("auth", authValue, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
                Path = "/",
                Secure = Request.IsHttps,
                Expires = DateTimeOffset.Now.AddDays(7)  // Cookie 本身设置较长的过期时间
            });

            Response.Cookies.Append("username", request.Username, new CookieOptions
            {
                HttpOnly = false,
                Expires = DateTimeOffset.Now.AddDays(7),
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
                Path = "/",
                Secure = Request.IsHttps
            });

            DicomLogger.Information("Api", "[API] 登录成功 - 用户名: {Username}", request.Username);
            return Ok();
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "[API] 登录异常 - 用户名: {Username}", request.Username);
            return StatusCode(500, "登录失败");
        }
    }

    [HttpPost("logout")]
    public IActionResult Logout()
    {
        try
        {
            var username = Request.Cookies["username"] ?? "unknown";
            Response.Cookies.Delete("auth", new CookieOptions { Path = "/" });
            Response.Cookies.Delete("username", new CookieOptions { Path = "/" });
            DicomLogger.Information("Api", "[API] 用户登出 - 用户名: {Username}", username);
            return Ok();
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "[API] 登出异常");
            return StatusCode(500, "登出失败");
        }
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
        try
        {
            var username = Request.Cookies["username"];
            if (string.IsNullOrEmpty(username))
            {
                DicomLogger.Warning("Api", "[API] 修改密码失败 - 原因: 未登录");
                return Unauthorized("未登录");
            }

            // 验证旧密码
            var isValid = await _repository.ValidateUserAsync(username, request.OldPassword);
            if (!isValid)
            {
                DicomLogger.Warning("Api", "[API] 修改密码失败 - 用户名: {Username}, 原因: 旧密码错误", username);
                return BadRequest("旧密码错误");
            }

            // 修改密码
            await _repository.ChangePasswordAsync(username, request.NewPassword);

            // 清除登录状态
            Response.Cookies.Delete("username");
            
            DicomLogger.Information("Api", "[API] 修改密码成功 - 用户名: {Username}", username);
            return Ok();
        }
        catch (Exception ex)
        {
            var username = Request.Cookies["username"] ?? "unknown";
            DicomLogger.Error("Api", ex, "[API] 修改密码异常 - 用户名: {Username}", username);
            return StatusCode(500, "修改密码失败");
        }
    }

    [HttpGet("check-session")]
    public IActionResult CheckSession()
    {
        // 中间件会自动处理会话验证
        // 如果会话有效，返回 200 OK
        // 如果会话无效，中间件会返回 401
        return Ok();
    }
}

public class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
} 