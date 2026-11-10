using System.ComponentModel.DataAnnotations;
using DatabaseSync.Core.Interfaces;
using DatabaseSync.Core.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DatabaseSync.Admin.Pages.Connections;

public class EditModel : PageModel
{
    private readonly ISyncConfigStore _configStore;

    public EditModel(ISyncConfigStore configStore)
    {
        _configStore = configStore;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsNew => string.IsNullOrWhiteSpace(Input.Id);

    public async Task<IActionResult> OnGetAsync(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            Input = new InputModel();
            return Page();
        }

        var config = await _configStore.GetAsync();
        var connection = config.Connections.FirstOrDefault(c => c.Id == id);
        if (connection is null)
        {
            TempData["ErrorMessage"] = "Connection not found.";
            return RedirectToPage("./Index");
        }

        Input = InputModel.FromConnection(connection);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var config = await _configStore.GetAsync();
        SyncConnection connection;

        if (string.IsNullOrWhiteSpace(Input.Id))
        {
            connection = new SyncConnection { Id = Guid.NewGuid().ToString("N") };
            config.Connections.Add(connection);
        }
        else
        {
            connection = config.Connections.FirstOrDefault(c => c.Id == Input.Id)
                ?? throw new InvalidOperationException("Connection not found.");
        }

        var previousPassword = connection.Password;
        Input.ApplyTo(connection);

        // Keep existing password when the edit form leaves password blank.
        if (string.IsNullOrWhiteSpace(Input.Password) && !string.IsNullOrWhiteSpace(previousPassword))
        {
            connection.Password = previousPassword;
        }

        if (Input.IsMain)
        {
            foreach (var item in config.Connections)
            {
                item.IsMain = item.Id == connection.Id;
            }
        }
        else if (config.Connections.All(c => !c.IsMain) && config.Connections.Count > 0)
        {
            config.Connections[0].IsMain = true;
        }

        await _configStore.SaveAsync(config);
        TempData["StatusMessage"] = $"Saved connection '{connection.Name}'.";
        return RedirectToPage("./Index");
    }

    public sealed class InputModel
    {
        public string? Id { get; set; }

        [Required, Display(Name = "Display name")]
        public string Name { get; set; } = string.Empty;

        [Display(Name = "Nominate as Main (source)")]
        public bool IsMain { get; set; }

        [Display(Name = "Enabled")]
        public bool IsEnabled { get; set; } = true;

        [Required]
        public string Server { get; set; } = string.Empty;

        [Required]
        public string Database { get; set; } = string.Empty;

        [Display(Name = "Windows authentication")]
        public bool IntegratedSecurity { get; set; }

        [Display(Name = "User ID")]
        public string? UserId { get; set; }

        [DataType(DataType.Password)]
        public string? Password { get; set; }

        [Display(Name = "Timeout (seconds)")]
        [Range(1, 300)]
        public int ConnectionTimeoutSeconds { get; set; } = 15;

        public bool Encrypt { get; set; } = true;

        [Display(Name = "Trust server certificate")]
        public bool TrustServerCertificate { get; set; } = true;

        public static InputModel FromConnection(SyncConnection connection) => new()
        {
            Id = connection.Id,
            Name = connection.Name,
            IsMain = connection.IsMain,
            IsEnabled = connection.IsEnabled,
            Server = connection.Server,
            Database = connection.Database,
            IntegratedSecurity = connection.IntegratedSecurity,
            UserId = connection.UserId,
            Password = string.Empty,
            ConnectionTimeoutSeconds = connection.ConnectionTimeoutSeconds,
            Encrypt = connection.Encrypt,
            TrustServerCertificate = connection.TrustServerCertificate
        };

        public void ApplyTo(SyncConnection connection)
        {
            connection.Name = Name.Trim();
            connection.IsMain = IsMain;
            connection.IsEnabled = IsEnabled;
            connection.Server = Server.Trim();
            connection.Database = Database.Trim();
            connection.IntegratedSecurity = IntegratedSecurity;
            connection.UserId = UserId?.Trim();
            connection.Password = Password;
            connection.ConnectionTimeoutSeconds = ConnectionTimeoutSeconds;
            connection.Encrypt = Encrypt;
            connection.TrustServerCertificate = TrustServerCertificate;
            connection.ApplicationName = "Coditium.DatabaseSync";
        }
    }
}
