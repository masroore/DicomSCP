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
        try
        {
            DicomLogger.Debug("Api", "[API] 尝试登录 - 用户名: {Username}", request.Username);

            var isValid = await _repository.ValidateUserAsync(request.Username, request.Password);
            if (!isValid)
            {
                DicomLogger.Warning("Api", "[API] 登录失败 - 用户名: {Username}, 原因: 用户名或密码错误", request.Username);
                return Unauthorized("用户名或密码错误");
            }

            // 创建身份认证票据
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, request.Username),
                new Claim("LoginTime", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")),
                new Claim("LastActivity", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
            };

            var claimsIdentity = new ClaimsIdentity(claims, "CustomAuth");
            var authProperties = new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.Now.AddMinutes(30),
                AllowRefresh = true,
                IssuedUtc = DateTimeOffset.Now,
            };

            await HttpContext.SignInAsync(
                "CustomAuth",
                new ClaimsPrincipal(claimsIdentity),
                authProperties);

            // 设置用户名 cookie 供前端使用
            Response.Cookies.Append("username", request.Username, new CookieOptions
            {
                HttpOnly = false,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                Secure = Request.IsHttps,
                Expires = DateTimeOffset.Now.AddMinutes(30)
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

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        try
        {
            var username = User.Identity?.Name ?? "unknown";
            await HttpContext.SignOutAsync("CustomAuth");
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

    [Authorize]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        try
        {
            var username = User.Identity?.Name;
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
            await HttpContext.SignOutAsync("CustomAuth");
            Response.Cookies.Delete("username", new CookieOptions { Path = "/" });
            
            DicomLogger.Information("Api", "[API] 修改密码成功 - 用户名: {Username}", username);
            return Ok();
        }
        catch (Exception ex)
        {
            var username = User.Identity?.Name ?? "unknown";
            DicomLogger.Error("Api", ex, "[API] 修改密码异常 - 用户名: {Username}", username);
            return StatusCode(500, "修改密码失败");
        }
    }

    [Authorize]
    [HttpGet("check-session")]
    public async Task<IActionResult> CheckSession()
    {
        try
        {
            // 更新最后活动时间
            var identity = User.Identity as ClaimsIdentity;
            var lastActivityClaim = identity?.FindFirst("LastActivity");
            if (lastActivityClaim != null)
            {
                var claims = new List<Claim>(User.Claims);
                claims.Remove(lastActivityClaim);
                claims.Add(new Claim("LastActivity", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));

                var newIdentity = new ClaimsIdentity(claims, "CustomAuth");
                var authProperties = new AuthenticationProperties
                {
                    IsPersistent = true,
                    ExpiresUtc = DateTimeOffset.Now.AddMinutes(30),
                    AllowRefresh = true,
                    IssuedUtc = DateTimeOffset.Now,
                };

                // 重新签发认证票据
                await HttpContext.SignInAsync("CustomAuth", 
                    new ClaimsPrincipal(newIdentity), 
                    authProperties);

                // 更新 username cookie
                Response.Cookies.Append("username", User.Identity?.Name ?? "", new CookieOptions
                {
                    HttpOnly = false,
                    SameSite = SameSiteMode.Lax,
                    Path = "/",
                    Secure = Request.IsHttps,
                    Expires = DateTimeOffset.Now.AddMinutes(30)
                });
            }

            return Ok(new { username = User.Identity?.Name });
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "[API] 检查会话状态失败");
            return StatusCode(500, "检查会话状态失败");
        }
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