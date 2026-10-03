using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Wfm.Domain;
using Wfm.Domain.Entities;
using Wfm.Domain.Enums;
using Wfm.Infrastructure.Identity;

namespace Wfm.Infrastructure.Data;

/// <summary>
/// Geliştirme ortamı için demo veri: iki örnek şirket (çiçekçi ve tamirci), kullanıcılar, görev tipleri ve görevler.
/// Tüm demo kullanıcıların parolası <see cref="DemoPassword"/>.
/// </summary>
public static class DbSeeder
{
    public const string DemoPassword = "Demo123!";
    public const string SuperAdminEmail = "admin@wfm.local";

    public static async Task SeedAsync(WfmDbContext db, UserManager<AppUser> users, RoleManager<AppRole> roles, bool demoData)
    {
        foreach (var r in Roles.All)
            if (!await roles.RoleExistsAsync(r))
                await roles.CreateAsync(new AppRole(r));

        if (await users.FindByEmailAsync(SuperAdminEmail) is null)
        {
            var system = new Tenant { Name = "Sistem", Slug = "system" };
            db.Tenants.Add(system);
            await db.SaveChangesAsync();
            await CreateUser(users, system.Id, SuperAdminEmail, "Sistem Yöneticisi", Roles.SuperAdmin);
        }

        if (!demoData || await db.Tenants.AnyAsync(t => t.Slug == "cicek-dunyasi")) return;

        await SeedFlorist(db, users);
        await SeedRepair(db, users);
    }

    private static async Task SeedFlorist(WfmDbContext db, UserManager<AppUser> users)
    {
        var t = new Tenant { Name = "Çiçek Dünyası", Slug = "cicek-dunyasi", DefaultLatitude = 40.9900, DefaultLongitude = 29.0280 };
        db.Tenants.Add(t);
        await db.SaveChangesAsync();

        var admin = await CreateUser(users, t.Id, "yonetici@cicek.local", "Ayşe Yılmaz", Roles.TenantAdmin);
        var dispatcher = await CreateUser(users, t.Id, "dispecer@cicek.local", "Mehmet Kaya", Roles.Dispatcher);
        var w1 = await CreateUser(users, t.Id, "kurye1@cicek.local", "Ali Demir", Roles.FieldWorker, "+905551112233");
        var w2 = await CreateUser(users, t.Id, "kurye2@cicek.local", "Zeynep Çelik", Roles.FieldWorker, "+905554445566");
        await CreateUser(users, t.Id, "izleyici@cicek.local", "Can Öztürk", Roles.Viewer);

        var team = new Team { TenantId = t.Id, Name = "Kadıköy Kuryeleri" };
        db.Teams.Add(team);
        w1.TeamId = w2.TeamId = team.Id;
        await users.UpdateAsync(w1);
        await users.UpdateAsync(w2);

        var delivery = new TaskType
        {
            TenantId = t.Id, Name = "Çiçek Teslimatı", Icon = "local_florist", Color = "#e91e63",
            Description = "Siparişi alıcıya teslim et.",
            Fields =
            [
                new() { Key = "recipient_name", Label = "Alıcı adı", Type = FieldType.Text, Required = true, Order = 1 },
                new() { Key = "flower_type", Label = "Çiçek türü", Type = FieldType.Select, Required = true, Order = 2,
                        Options = ["Gül buketi", "Orkide", "Papatya", "Karışık aranjman"] },
                new() { Key = "card_note", Label = "Kart notu", Type = FieldType.TextArea, Order = 3 },
                new() { Key = "received_by", Label = "Teslim alan kişi", Type = FieldType.Text, Required = true,
                        FilledOnCompletion = true, Order = 4 },
            ],
            Completion = new() { RequireSignature = true, RequirePhoto = true, MinPhotoCount = 1 }
        };
        var pickup = new TaskType
        {
            TenantId = t.Id, Name = "Tedarikçiden Alım", Icon = "inventory_2", Color = "#8e24aa",
            Fields = [new() { Key = "supplier", Label = "Tedarikçi", Type = FieldType.Text, Required = true, Order = 1 },
                      new() { Key = "item_count", Label = "Adet", Type = FieldType.Number, Required = true, Order = 2 }],
            Completion = new() { RequirePhoto = true, MinPhotoCount = 1 }
        };
        db.TaskTypes.AddRange(delivery, pickup);

        var places = new (string Address, double Lat, double Lng)[]
        {
            ("Caferağa Mah. Moda Cad. No:12, Kadıköy", 40.9862, 29.0256),
            ("Bağdat Cad. No:250, Kadıköy", 40.9671, 29.0626),
            ("Acıbadem Mah. Çeçen Sok. No:5, Üsküdar", 41.0045, 29.0448),
            ("Fenerbahçe Mah. Fener Kalamış Cad. No:40", 40.9752, 29.0391),
        };
        var flowers = new[] { "Gül buketi", "Orkide", "Papatya", "Karışık aranjman" };
        for (var i = 0; i < places.Length; i++)
        {
            var task = new WorkTask
            {
                TenantId = t.Id, TaskTypeId = delivery.Id, CreatedById = dispatcher.Id,
                Title = $"Sipariş #{1001 + i} teslimatı", Address = places[i].Address,
                Latitude = places[i].Lat, Longitude = places[i].Lng,
                CustomerName = $"Müşteri {i + 1}", CustomerPhone = "+90555000000" + i,
                Priority = i == 0 ? TaskPriority.Urgent : TaskPriority.Normal,
                ScheduledStart = DateTime.UtcNow.Date.AddHours(9 + i), ScheduledEnd = DateTime.UtcNow.Date.AddHours(10 + i),
                CustomFieldValues = new() { ["recipient_name"] = $"Alıcı {i + 1}", ["flower_type"] = flowers[i], ["card_note"] = "İyi ki varsın!" }
            };
            if (i < 3) task.Assign(i % 2 == 0 ? w1.Id : w2.Id, dispatcher.Id);
            db.Tasks.Add(task);
        }
        await db.SaveChangesAsync();
        _ = admin;
    }

    private static async Task SeedRepair(WfmDbContext db, UserManager<AppUser> users)
    {
        var t = new Tenant { Name = "Hızlı Tamir", Slug = "hizli-tamir", DefaultLatitude = 41.0370, DefaultLongitude = 28.9850 };
        db.Tenants.Add(t);
        await db.SaveChangesAsync();

        await CreateUser(users, t.Id, "yonetici@tamir.local", "Hakan Arslan", Roles.TenantAdmin);
        var dispatcher = await CreateUser(users, t.Id, "dispecer@tamir.local", "Elif Şahin", Roles.Dispatcher);
        var tech = await CreateUser(users, t.Id, "teknisyen1@tamir.local", "Burak Yıldız", Roles.FieldWorker, "+905557778899");
        await CreateUser(users, t.Id, "teknisyen2@tamir.local", "Selin Aydın", Roles.FieldWorker);

        var repair = new TaskType
        {
            TenantId = t.Id, Name = "Arıza Onarımı", Icon = "build", Color = "#f57c00",
            Description = "Müşteri adresindeki arızayı gider.",
            Fields =
            [
                new() { Key = "device", Label = "Cihaz", Type = FieldType.Select, Required = true, Order = 1,
                        Options = ["Kombi", "Klima", "Çamaşır makinesi", "Bulaşık makinesi", "Buzdolabı"] },
                new() { Key = "fault_code", Label = "Arıza kodu", Type = FieldType.Text, Order = 2 },
                new() { Key = "fault_description", Label = "Arıza açıklaması", Type = FieldType.TextArea, Required = true, Order = 3 },
                new() { Key = "parts_used", Label = "Kullanılan parçalar", Type = FieldType.TextArea, FilledOnCompletion = true, Order = 4 },
                new() { Key = "labor_hours", Label = "İşçilik (saat)", Type = FieldType.Number, Required = true, FilledOnCompletion = true, Order = 5 },
                new() { Key = "warranty", Label = "Garanti kapsamında", Type = FieldType.Checkbox, FilledOnCompletion = true, Order = 6 },
            ],
            Completion = new() { RequireSignature = true, RequirePhoto = true, MinPhotoCount = 2, RequireNote = true }
        };
        var install = new TaskType
        {
            TenantId = t.Id, Name = "Montaj", Icon = "handyman", Color = "#0097a7",
            Fields = [new() { Key = "product", Label = "Ürün", Type = FieldType.Text, Required = true, Order = 1 }],
            Completion = new() { RequireSignature = true }
        };
        db.TaskTypes.AddRange(repair, install);

        var task = new WorkTask
        {
            TenantId = t.Id, TaskTypeId = repair.Id, CreatedById = dispatcher.Id,
            Title = "Kombi su akıtıyor", Address = "Cihangir Mah. Akarsu Yokuşu No:20, Beyoğlu",
            Latitude = 41.0317, Longitude = 28.9833, CustomerName = "Deniz Koç", CustomerPhone = "+905550001122",
            Priority = TaskPriority.High,
            CustomFieldValues = new() { ["device"] = "Kombi", ["fault_code"] = "F28", ["fault_description"] = "Alttan su damlatıyor." }
        };
        task.Assign(tech.Id, dispatcher.Id);
        db.Tasks.Add(task);
        db.Tasks.Add(new WorkTask
        {
            TenantId = t.Id, TaskTypeId = install.Id, CreatedById = dispatcher.Id,
            Title = "Klima montajı", Address = "Teşvikiye Mah. Valikonağı Cad. No:80, Şişli",
            Latitude = 41.0510, Longitude = 28.9940, CustomerName = "Murat Er",
            CustomFieldValues = new() { ["product"] = "12000 BTU Split Klima" }
        });
        await db.SaveChangesAsync();
    }

    private static async Task<AppUser> CreateUser(UserManager<AppUser> users, Guid tenantId, string email, string name,
        string role, string? phone = null)
    {
        var u = new AppUser { TenantId = tenantId, Email = email, UserName = email, FullName = name, PhoneNumber = phone, EmailConfirmed = true };
        var res = await users.CreateAsync(u, DemoPassword);
        if (!res.Succeeded) throw new InvalidOperationException(string.Join("; ", res.Errors.Select(e => e.Description)));
        await users.AddToRoleAsync(u, role);
        return u;
    }
}
