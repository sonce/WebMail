using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Localization;
using WebMail;
using WebMail.Domain;
using WebMail.Services;

namespace WebMail.Pages.Admin;

[Authorize(Policy = "AdminOnly")]
public class SuppliersModel : SalesModel
{
    public SuppliersModel(UserAdminService users, IStringLocalizer<SharedResource> loc) : base(users, loc)
    {
    }

    protected override UserRole Role => UserRole.Supplier;
}
