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
    /// <summary>Ürün adı: giriş ekranı, sekme başlıkları ve mobil uygulama aynı adı kullanır.</summary>
    public const string ProductName = "WFM Saha Yönetimi";

    /// <summary>Durum görünümü: etiket, harita pini ve zaman çizelgesi aynı kaynaktan beslenir.</summary>
    public record StatusStyle(string Hex, string Fg, string Icon);

    public static StatusStyle StatusLook(WorkTaskStatus s) => s switch
    {
        WorkTaskStatus.Draft => new("#546e7a", "#fff", "edit_note"),
        WorkTaskStatus.Assigned => new("#0277bd", "#fff", "assignment_ind"),
        WorkTaskStatus.Accepted => new("#3949ab", "#fff", "thumb_up"),
        WorkTaskStatus.EnRoute => new("#7b1fa2", "#fff", "directions_car"),
        WorkTaskStatus.OnSite => new("#ffb300", "#212121", "place"),
        WorkTaskStatus.Completed => new("#2e7d32", "#fff", "check_circle"),
        WorkTaskStatus.Rejected => new("#c62828", "#fff", "block"),
        WorkTaskStatus.Failed => new("#ad1457", "#fff", "error"),
        WorkTaskStatus.Cancelled => new("#424242", "#fff", "cancel"),
        _ => new("#424242", "#fff", "help")
    };

    /// <summary>Harita işaretçileri ve grafikler için hex renk.</summary>
    public static string StatusHex(WorkTaskStatus s) => StatusLook(s).Hex;

    public static bool IsClosed(WorkTaskStatus s) => s is WorkTaskStatus.Completed or WorkTaskStatus.Cancelled or WorkTaskStatus.Failed;

    public static readonly WorkTaskStatus[] FlowOrder =
    [
        WorkTaskStatus.Draft, WorkTaskStatus.Assigned, WorkTaskStatus.Accepted, WorkTaskStatus.EnRoute, WorkTaskStatus.OnSite,
        WorkTaskStatus.Completed, WorkTaskStatus.Rejected, WorkTaskStatus.Failed, WorkTaskStatus.Cancelled
    ];

    public static string Status(WorkTaskStatus s) => TaskStatusFlow.DisplayName(s);

    public static string Priority(TaskPriority p) => p switch
    {
        TaskPriority.Low => "Düşük",
        TaskPriority.Normal => "Normal",
        TaskPriority.High => "Yüksek",
        TaskPriority.Urgent => "Acil",
        _ => p.ToString()
    };

    public static string PriorityHex(TaskPriority p) => p switch
    {
        TaskPriority.Urgent => "#c62828",
        TaskPriority.High => "#ef6c00",
        TaskPriority.Low => "#78909c",
        _ => "#0277bd"
    };

    public static string PriorityIcon(TaskPriority p) => p switch
    {
        TaskPriority.Urgent => Icons.Material.Filled.PriorityHigh,
        TaskPriority.High => Icons.Material.Filled.KeyboardDoubleArrowUp,
        TaskPriority.Low => Icons.Material.Filled.KeyboardArrowDown,
        _ => Icons.Material.Filled.Remove
    };

    public static string NotificationIcon(string kind) => kind switch
    {
        NotificationKinds.Assigned => Icons.Material.Filled.AssignmentInd,
        NotificationKinds.Unassigned => Icons.Material.Filled.AssignmentReturn,
        NotificationKinds.Cancelled => Icons.Material.Filled.Cancel,
        NotificationKinds.Rejected => Icons.Material.Filled.Block,
        NotificationKinds.Failed => Icons.Material.Filled.ErrorOutline,
        NotificationKinds.Overdue => Icons.Material.Filled.AlarmOn,
        NotificationKinds.Comment => Icons.Material.Filled.ChatBubbleOutline,
        NotificationKinds.StageChanged => Icons.Material.Filled.Flag,
        NotificationKinds.PasswordReset => Icons.Material.Filled.LockReset,
        _ => Icons.Material.Filled.Notifications
    };

    public static Color NotificationColor(string kind) => kind switch
    {
        NotificationKinds.Cancelled or NotificationKinds.Rejected or NotificationKinds.Failed => Color.Error,
        NotificationKinds.Overdue or NotificationKinds.PasswordReset => Color.Warning,
        NotificationKinds.Comment => Color.Secondary,
        _ => Color.Primary
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
        if (d.TotalSeconds < 0) return "az önce";
        if (d.TotalSeconds < 60) return "az önce";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} dk önce";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} sa önce";
        return $"{(int)d.TotalDays} gün önce";
    }

    /// <summary>"12 dk" / "2 sa 5 dk" biçiminde süre.</summary>
    public static string Duration(TimeSpan d) =>
        d.TotalMinutes < 60 ? $"{Math.Max(1, (int)d.TotalMinutes)} dk"
        : d.TotalHours < 24 ? $"{(int)d.TotalHours} sa {d.Minutes} dk"
        : $"{(int)d.TotalDays} gün {d.Hours} sa";

    /// <summary>"Bugün", "Dün" ya da tarih; bildirim ve geçmiş gruplamasında kullanılır.</summary>
    public static string DayLabel(DateTime utc)
    {
        var day = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().Date;
        var today = DateTime.Now.Date;
        if (day == today) return "Bugün";
        if (day == today.AddDays(-1)) return "Dün";
        return day.ToString("d MMMM yyyy, dddd", Tr);
    }

    public static readonly System.Globalization.CultureInfo Tr = new("tr-TR");

    /// <summary>Koordinatı noktalı ondalıkla yazar (Türkçe virgül, enlem/boylam ayracıyla karışmasın).</summary>
    public static string Coord(double? lat, double? lng, int digits = 4)
    {
        var f = "0." + new string('0', digits);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return $"{lat?.ToString(f, inv)}, {lng?.ToString(f, inv)}";
    }

    public static string Distance(double meters) =>
        meters < 1000 ? $"{meters:0} m" : $"{meters / 1000:0.0} km";

    public static string Initials(string name) =>
        string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => char.ToUpper(p[0])));

    public static readonly string[] IconChoices =
    [
        "assignment", "local_florist", "local_shipping", "build", "handyman", "plumbing", "electrical_services",
        "cleaning_services", "inventory_2", "restaurant", "medical_services", "home_repair_service", "construction",
        "pest_control", "local_laundry_service", "ac_unit", "router", "security", "pets", "storefront", "description"
    ];

    public static readonly string[] ColorChoices =
    [
        "#1976d2", "#e91e63", "#f57c00", "#0097a7", "#8e24aa", "#2e7d32", "#c62828", "#5d4037", "#455a64", "#fbc02d"
    ];

    public const string DefaultPrimary = "#1565c0";
    public const string DefaultSecondary = "#00897b";

    /// <summary>Şirketin marka rengine göre açık ve koyu temayı üretir; yüksek kontrastta metin ve çizgiler koyulaşır.</summary>
    public static MudTheme BuildTheme(string? brand, bool highContrast = false)
    {
        var primary = string.IsNullOrWhiteSpace(brand) ? DefaultPrimary : brand;
        string darkPrimary, contrastPrimary;
        try
        {
            var c = new MudBlazor.Utilities.MudColor(primary);
            darkPrimary = c.ColorLighten(0.25).ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex);
            // Beyaz zeminde en az ~7:1 hedefi: marka rengini koyulaştır.
            contrastPrimary = c.ColorDarken(0.18).ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex);
        }
        catch { (darkPrimary, contrastPrimary) = ("#64b5f6", "#0d3c8c"); }

        var light = new PaletteLight
        {
            Primary = highContrast ? contrastPrimary : primary,
            Secondary = highContrast ? "#00574b" : DefaultSecondary,
            AppbarBackground = highContrast ? contrastPrimary : primary,
            Background = highContrast ? "#ffffff" : "#f5f7fa",
            TextPrimary = highContrast ? "#000000" : "rgba(0,0,0,0.87)",
            TextSecondary = highContrast ? "#1f1f1f" : "rgba(0,0,0,0.62)",
        };
        if (highContrast)
        {
            light.LinesDefault = "#4a4a4a";
            light.LinesInputs = "#000000";
            light.TableLines = "#6b6b6b";
            light.Divider = "#4a4a4a";
            light.Success = "#1b5e20";
            light.Error = "#a00000";
            light.Warning = "#8a4b00";
            light.Info = "#01497c";
        }

        var dark = new PaletteDark
        {
            Primary = highContrast ? "#9cd0ff" : darkPrimary,
            Secondary = highContrast ? "#7ff0e0" : "#4db6ac",
            AppbarBackground = highContrast ? "#000000" : "#1e2430",
            Background = highContrast ? "#000000" : "#12161d",
            Surface = highContrast ? "#0a0a0a" : "#1b2029",
            DrawerBackground = highContrast ? "#000000" : "#1b2029",
            TextPrimary = highContrast ? "#ffffff" : "rgba(255,255,255,0.87)",
            TextSecondary = highContrast ? "#e6e6e6" : "rgba(255,255,255,0.68)",
        };
        if (highContrast)
        {
            dark.LinesDefault = "#bdbdbd";
            dark.LinesInputs = "#ffffff";
            dark.TableLines = "#9e9e9e";
            dark.Divider = "#bdbdbd";
        }

        return new MudTheme
        {
            PaletteLight = light,
            PaletteDark = dark,
            LayoutProperties = new LayoutProperties { DefaultBorderRadius = "8px" }
        };
    }

    public static MudTheme Theme { get; } = BuildTheme(null);
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
        if (singleton)
        {
            services.AddSingleton<ThemeState>();
            services.AddSingleton<LayoutState>();
            services.AddSingleton<ArrivalDetector>();
        }
        else
        {
            services.AddScoped<ThemeState>();
            services.AddScoped<LayoutState>();
            services.AddScoped<ArrivalDetector>();
        }
        return services;
    }
}
