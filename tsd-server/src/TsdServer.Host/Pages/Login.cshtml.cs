using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TsdServer.Core;
using TsdServer.Infrastructure;

namespace TsdServer.Host.Pages;

public sealed class LoginModel : PageModel
{
    readonly PortalDb _db;
    public string? Error { get; private set; }

    public LoginModel(PortalDb db) => _db = db;

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(string login, string password)
    {
        var user = await _db.Users.FirstOrDefaultAsync(x => x.Login == login);
        if (user is null || !Passwords.Verify(password, user.PasswordHash))
        {
            Error = "Неверный логин или пароль";
            return Page();
        }
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, user.Login),
            new Claim(ClaimTypes.Role, user.Role)
        };
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
        _db.Audit.Add(new AuditEntry { At = DateTime.UtcNow, User = user.Login, Action = "login", Detail = "" });
        await _db.SaveChangesAsync();
        return Redirect("/");
    }
}
