namespace Wfm.Domain;

public static class Roles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string TenantAdmin = "TenantAdmin";
    public const string Dispatcher = "Dispatcher";
    public const string FieldWorker = "FieldWorker";
    public const string Viewer = "Viewer";

    public static readonly string[] All = [SuperAdmin, TenantAdmin, Dispatcher, FieldWorker, Viewer];

    /// <summary>Bir kiracı yöneticisinin atayabileceği roller (SuperAdmin hariç).</summary>
    public static readonly string[] TenantAssignable = [TenantAdmin, Dispatcher, FieldWorker, Viewer];

    public static string DisplayName(string role) => role switch
    {
        SuperAdmin => "Sistem Yöneticisi",
        TenantAdmin => "Şirket Yöneticisi",
        Dispatcher => "Dispeçer",
        FieldWorker => "Saha Çalışanı",
        Viewer => "İzleyici",
        _ => role
    };
}

public static class Policies
{
    public const string ManageTasks = "CanManageTasks";
    public const string ManageUsers = "CanManageUsers";
    public const string ManageTaskTypes = "CanManageTaskTypes";
    public const string ViewTracking = "CanViewTracking";
    public const string ViewReports = "CanViewReports";
    public const string FieldWork = "CanDoFieldWork";
    public const string ManageTenants = "CanManageTenants";

    /// <summary>Politika → izin verilen roller. API ve UI aynı tabloyu kullanır.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Map = new Dictionary<string, string[]>
    {
        [ManageTasks] = [Roles.SuperAdmin, Roles.TenantAdmin, Roles.Dispatcher],
        [ManageUsers] = [Roles.SuperAdmin, Roles.TenantAdmin],
        [ManageTaskTypes] = [Roles.SuperAdmin, Roles.TenantAdmin],
        [ViewTracking] = [Roles.SuperAdmin, Roles.TenantAdmin, Roles.Dispatcher, Roles.Viewer],
        [ViewReports] = [Roles.SuperAdmin, Roles.TenantAdmin, Roles.Dispatcher, Roles.Viewer],
        [FieldWork] = [Roles.FieldWorker],
        [ManageTenants] = [Roles.SuperAdmin],
    };
}

public static class WfmClaims
{
    public const string TenantId = "tenant_id";
    public const string TenantName = "tenant_name";
    public const string FullName = "full_name";
}
