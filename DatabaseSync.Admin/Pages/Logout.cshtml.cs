using DatabaseSync.Admin.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DatabaseSync.Admin.Pages;

public class LogoutModel : PageModel
{
    private readonly AuditService _audit;

    public LogoutModel(AuditService audit)
    {
        _audit = audit;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        await _audit.WriteAsync(User, "Logout", "Auth", "Signed out.");
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToPage("/Login");
    }
}
