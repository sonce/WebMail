using Microsoft.AspNetCore.Authorization;
using WebMail.Domain;
using WebMail.Services;

namespace WebMail.Pages.Admin;

[Authorize(Policy = "AdminOnly")]
public class SuppliersModel : SalesModel
{
    public SuppliersModel(UserAdminService users) : base(users)
    {
    }

    protected override UserRole Role => UserRole.Supplier;
}
