using System.ComponentModel.DataAnnotations;
using DatabaseSync.Admin.Services;
using DatabaseSync.Core.Interfaces;
using DatabaseSync.Core.Models.Management;
using DatabaseSync.Infrastructure.Management;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DatabaseSync.Admin.Pages;

[Authorize(Roles = "Admin")]
public class UsersModel : PageModel
{
    private readonly IManagementStore _store;
    private readonly AuditService _audit;

    public UsersModel(IManagementStore store, AuditService audit)
    {
        _store = store;
        _audit = audit;
    }

    public IReadOnlyList<AppUser> Users { get; private set; } = [];

    public string DatabasePath { get; private set; } = string.Empty;

    [BindProperty]
    public CreateInput NewUser { get; set; } = new();

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        await LoadAsync();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var existing = await _store.GetUserByUserNameAsync(NewUser.UserName.Trim());
        if (existing is not null)
        {
            ModelState.AddModelError(string.Empty, "Username already exists.");
            return Page();
        }

        var now = DateTimeOffset.UtcNow;
        var (hash, salt) = PasswordHasher.HashPassword(NewUser.Password);
        var id = await _store.CreateUserAsync(new AppUser
        {
            UserName = NewUser.UserName.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(NewUser.DisplayName) ? NewUser.UserName.Trim() : NewUser.DisplayName.Trim(),
            PasswordHash = hash,
            PasswordSalt = salt,
            Role = NewUser.Role,
            IsActive = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });

        await _audit.WriteAsync(User, "CreateUser", "Users", $"Created user '{NewUser.UserName.Trim()}' (Id={id}, Role={NewUser.Role}).");
        TempData["StatusMessage"] = $"User '{NewUser.UserName.Trim()}' created.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleAsync(long id)
    {
        var user = await _store.GetUserByIdAsync(id);
        if (user is null)
        {
            TempData["ErrorMessage"] = "User not found.";
            return RedirectToPage();
        }

        if (string.Equals(user.UserName, User.Identity?.Name, StringComparison.OrdinalIgnoreCase) && user.IsActive)
        {
            TempData["ErrorMessage"] = "You cannot deactivate your own account.";
            return RedirectToPage();
        }

        await _store.SetUserActiveAsync(id, !user.IsActive);
        await _audit.WriteAsync(User, user.IsActive ? "DeactivateUser" : "ActivateUser", "Users", $"User '{user.UserName}'.");
        TempData["StatusMessage"] = $"User '{user.UserName}' is now {(user.IsActive ? "inactive" : "active")}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResetPasswordAsync(long id, string newPassword)
    {
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 6)
        {
            TempData["ErrorMessage"] = "Password must be at least 6 characters.";
            return RedirectToPage();
        }

        var user = await _store.GetUserByIdAsync(id);
        if (user is null)
        {
            TempData["ErrorMessage"] = "User not found.";
            return RedirectToPage();
        }

        var (hash, salt) = PasswordHasher.HashPassword(newPassword);
        user.PasswordHash = hash;
        user.PasswordSalt = salt;
        user.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _store.UpdateUserAsync(user);
        await _audit.WriteAsync(User, "ResetPassword", "Users", $"Password reset for '{user.UserName}'.");
        TempData["StatusMessage"] = $"Password updated for '{user.UserName}'.";
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        DatabasePath = _store.DatabasePath;
        Users = await _store.ListUsersAsync();
    }

    public sealed class CreateInput
    {
        [Required]
        [Display(Name = "Username")]
        public string UserName { get; set; } = string.Empty;

        [Display(Name = "Display name")]
        public string DisplayName { get; set; } = string.Empty;

        [Required]
        [MinLength(6)]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [Required]
        public string Role { get; set; } = "Operator";
    }
}
