using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Wfm.Domain.Entities;
using Wfm.Domain.Enums;
using FieldType = Wfm.Domain.Enums.FieldType;

namespace Wfm.Shared.UI.Services;

public static class Ui
{
    public static Color StatusColor(WorkTaskStatus s) => s switch
    {
        WorkTaskStatus.Draft => Color.Default,
        WorkTaskStatus.Assigned => Color.Info,
        WorkTaskStatus.Accepted => Color.Primary,
        WorkTaskStatus.EnRoute => Color.Secondary,
        WorkTaskStatus.OnSite => Color.Warning,
        WorkTaskStatus.Completed => Color.Success,
        WorkTaskStatus.Rejected or WorkTaskStatus.Failed => Color.Error,
        WorkTaskStatus.Cancelled => Color.Dark,
        _ => Color.Default
    };

    /// <summary>Harita işaretçileri için hex renk.</summary>
    public static string StatusHex(WorkTaskStatus s) => s switch
    {
        WorkTaskStatus.Draft => "#9e9e9e",
        WorkTaskStatus.Assigned => "#0288d1",
        WorkTaskStatus.Accepted => "#3949ab",
        WorkTaskStatus.EnRoute => "#8e24aa",
        WorkTaskStatus.OnSite => "#f57c00",
        WorkTaskStatus.Completed => "#2e7d32",
        WorkTaskStatus.Rejected or WorkTaskStatus.Failed => "#c62828",
        _ => "#424242"
    };

    public static string Status(WorkTaskStatus s) => TaskStatusFlow.DisplayName(s);

    public static string Priority(TaskPriority p) => p switch
    {
        TaskPriority.Low => "Düşük",
        TaskPriority.Normal => "Normal",
        TaskPriority.High => "Yüksek",
        TaskPriority.Urgent => "Acil",
        _ => p.ToString()
    };

    public static Color PriorityColor(TaskPriority p) => p switch
    {
        TaskPriority.Urgent => Color.Error,
        TaskPriority.High => Color.Warning,
        TaskPriority.Low => Color.Default,
        _ => Color.Info
    };

    public static string FieldTypeName(FieldType t) => t switch
    {
        FieldType.Text => "Metin",
        FieldType.Number => "Sayı",
        FieldType.Select => "Seçim listesi",
        FieldType.Date => "Tarih",
        FieldType.Checkbox => "Onay kutusu",
        FieldType.TextArea => "Uzun metin",
        FieldType.Phone => "Telefon",
        _ => t.ToString()
    };

    public static string Local(DateTime? utc, string format = "dd.MM.yyyy HH:mm") =>
        utc is null ? "-" : DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc).ToLocalTime().ToString(format);

    public static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        if (d.TotalSeconds < 60) return "az önce";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} dk önce";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} sa önce";
        return $"{(int)d.TotalDays} gün önce";
    }

    public static string Initials(string name) =>
        string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => char.ToUpper(p[0])));

    public static readonly string[] IconChoices =
    [
        "assignment", "local_florist", "local_shipping", "build", "handyman", "plumbing", "electrical_services",
        "cleaning_services", "inventory_2", "restaurant", "medical_services", "home_repair_service", "construction",
        "pest_control", "local_laundry_service", "ac_unit", "router", "security", "pets", "storefront"
    ];

    public static readonly string[] ColorChoices =
    [
        "#1976d2", "#e91e63", "#f57c00", "#0097a7", "#8e24aa", "#2e7d32", "#c62828", "#5d4037", "#455a64", "#fbc02d"
    ];

    public static MudTheme Theme { get; } = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#1565c0",
            Secondary = "#00897b",
            AppbarBackground = "#1565c0",
            Background = "#f5f7fa",
        },
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "8px" }
    };
}

public static class UiServiceCollectionExtensions
{
    /// <summary>Ortak UI servisleri. Web'de scoped (her tarayıcı bağlantısı), mobilde singleton.</summary>
    public static IServiceCollection AddWfmUi(this IServiceCollection services, bool singleton)
    {
        services.AddMudServices(c =>
        {
            c.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomCenter;
            c.SnackbarConfiguration.VisibleStateDuration = 4000;
        });
        services.AddAuthorizationCore();
        services.AddCascadingAuthenticationState();
        if (singleton)
        {
            services.AddSingleton<WfmAuthStateProvider>();
            services.AddSingleton<AuthenticationStateProvider>(sp => sp.GetRequiredService<WfmAuthStateProvider>());
            services.AddSingleton<UserContext>();
            services.AddSingleton<NotificationCenter>();
        }
        else
        {
            services.AddScoped<WfmAuthStateProvider>();
            services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<WfmAuthStateProvider>());
            services.AddScoped<UserContext>();
            services.AddScoped<NotificationCenter>();
        }
        services.AddScoped<WfmJs>();
        return services;
    }
}
