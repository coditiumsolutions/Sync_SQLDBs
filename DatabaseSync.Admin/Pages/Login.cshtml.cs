using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using DatabaseSync.Core.Interfaces;
using DatabaseSync.Core.Models.Management;
using DatabaseSync.Infrastructure.Management;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DatabaseSync.Admin.Pages;

[AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly IManagementStore _managementStore;

    public LoginModel(IManagementStore managementStore)
    {
        _managementStore = managementStore;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? ErrorMessage { get; private set; }

    public void OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            Response.Redirect("/");
        }
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var userName = Input.UserName.Trim();
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var agent = Request.Headers.UserAgent.ToString();

        var user = await _managementStore.GetUserByUserNameAsync(userName);
        var ok = user is not null
                 && user.IsActive
                 && PasswordHasher.Verify(Input.Password, user.PasswordHash, user.PasswordSalt);

        await _managementStore.RecordLoginAttemptAsync(new LoginAttemptRecord
        {
            UserName = userName,
            Succeeded = ok,
            IpAddress = ip,
            UserAgent = Truncate(agent, 400),
            Detail = ok ? "Login succeeded." : (user is null ? "Unknown user." : user.IsActive ? "Invalid password." : "User inactive."),
            CreatedAtUtc = DateTimeOffset.UtcNow
        });

        if (!ok || user is null)
        {
            ErrorMessage = "Invalid username or password.";
            return Page();
        }

        user.LastLoginAtUtc = DateTimeOffset.UtcNow;
        user.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _managementStore.UpdateUserAsync(user);

        await _managementStore.WriteAuditAsync(new AuditEventRecord
        {
            UserId = user.Id,
            UserName = user.UserName,
            Action = "Login",
            Category = "Auth",
            Detail = $"Signed in from {ip}",
            CreatedAtUtc = DateTimeOffset.UtcNow
        });

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName),
            new(ClaimTypes.GivenName, user.DisplayName),
            new(ClaimTypes.Role, user.Role)
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = Input.RememberMe,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(Input.RememberMe ? 72 : 12)
            });

        return RedirectToPage("/Index");
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? value : value.Length <= max ? value : value[..max];

    public sealed class InputModel
    {
        [Required]
        [Display(Name = "Username")]
        public string UserName { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [Display(Name = "Remember me")]
        public bool RememberMe { get; set; }
    }
}
