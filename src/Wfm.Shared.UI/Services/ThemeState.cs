using System.Text.Json;
using MudBlazor;
using Wfm.Application.Contracts;

namespace Wfm.Shared.UI.Services;

/// <summary>
/// Açık/koyu tema ve yüksek kontrast tercihi ile şirket markası (renk, logo). Tercihler cihazda saklanır; son giriş
/// yapılan şirketin markası da hatırlanır ki giriş ekranı şirkete özel görünsün.
/// </summary>
public sealed class ThemeState
{
    private const string ModeKey = "wfm.theme";
    private const string ContrastKey = "wfm.contrast";
    private const string BrandKey = "wfm.brand";

    /// <summary>"light", "dark" ya da "system".</summary>
    public string Mode { get; private set; } = "system";
    public bool SystemDark { get; private set; }
    public bool HighContrast { get; private set; }
    public bool IsDark => Mode == "dark" || (Mode == "system" && SystemDark);

    public string? BrandColor { get; private set; }
    public string? LogoUrl { get; private set; }
    public string? TenantName { get; private set; }
    public string? TenantSlug { get; private set; }
    public MudTheme Theme { get; private set; } = Ui.Theme;

    /// <summary>Kök elemana eklenen sınıflar (koyu harita karoları, kontrast kuralları).</summary>
    public string CssClass => string.Join(' ', new[] { IsDark ? "wfm-dark" : null, HighContrast ? "wfm-contrast" : null }.OfType<string>());

    public event Action? Changed;

    private bool _loaded;

    public async Task LoadAsync(WfmJs js)
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            var saved = await js.Invoke<string?>("storageGet", ModeKey);
            if (saved is "light" or "dark" or "system") Mode = saved;
            HighContrast = await js.Invoke<string?>("storageGet", ContrastKey) == "1";
            if (TenantName is null && await js.Invoke<string?>("storageGet", BrandKey) is { Length: > 0 } json &&
                JsonSerializer.Deserialize<PublicTenantBrandDto>(json) is { } brand)
                ApplyBrand(brand.Name, brand.Slug, brand.BrandColor, brand.LogoUrl);
        }
        catch { /* depolama yoksa varsayılan */ }
        Rebuild();
    }

    public async Task SetModeAsync(WfmJs js, string mode)
    {
        Mode = mode;
        await js.Run("storageSet", ModeKey, mode);
        Changed?.Invoke();
    }

    public async Task SetHighContrastAsync(WfmJs js, bool on)
    {
        HighContrast = on;
        await js.Run("storageSet", ContrastKey, on ? "1" : null);
        Rebuild();
    }

    public void SetSystemDark(bool dark)
    {
        if (SystemDark == dark) return;
        SystemDark = dark;
        Changed?.Invoke();
    }

    /// <summary>Oturumdaki şirketin markasını uygular ve giriş ekranı için cihazda hatırlar.</summary>
    public async Task SetTenantAsync(WfmJs js, TenantDto tenant)
    {
        SetTenant(tenant);
        await js.Run("storageSet", BrandKey,
            JsonSerializer.Serialize(new PublicTenantBrandDto(tenant.Name, tenant.Slug, tenant.BrandColor, tenant.LogoUrl)));
    }

    public void SetTenant(TenantDto? tenant)
    {
        ApplyBrand(tenant?.Name, tenant?.Slug, tenant?.BrandColor, tenant?.LogoUrl);
        Rebuild();
    }

    /// <summary>Giriş ekranında ?sirket= ile gelen ya da hatırlanan marka.</summary>
    public void SetBrand(PublicTenantBrandDto? brand)
    {
        ApplyBrand(brand?.Name, brand?.Slug, brand?.BrandColor, brand?.LogoUrl);
        Rebuild();
    }

    /// <summary>Cihazda hatırlanan son şirket markasını yükler (giriş ekranı).</summary>
    public async Task RestoreBrandAsync(WfmJs js)
    {
        try
        {
            if (await js.Invoke<string?>("storageGet", BrandKey) is { Length: > 0 } json &&
                JsonSerializer.Deserialize<PublicTenantBrandDto>(json) is { } brand)
                SetBrand(brand);
        }
        catch { /* bozuk kayıt: varsayılan marka */ }
    }

    public async Task ForgetBrandAsync(WfmJs js)
    {
        await js.Run("storageSet", BrandKey, null);
        SetBrand(null);
    }

    private void ApplyBrand(string? name, string? slug, string? color, string? logo) =>
        (TenantName, TenantSlug, BrandColor, LogoUrl) = (name, slug, color, logo);

    private void Rebuild()
    {
        Theme = Ui.BuildTheme(BrandColor, HighContrast);
        Changed?.Invoke();
    }
}

/// <summary>Sayfaların üst bara ilettiği bilgi (saha arayüzünde sayfa başlığı).</summary>
public sealed class LayoutState
{
    public string? Title { get; private set; }
    public event Action? Changed;

    public void SetTitle(string? title)
    {
        if (title == Title) return;
        Title = title;
        Changed?.Invoke();
    }
}
