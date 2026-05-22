using CareerPanda.DataAccess.Entities.Cp;

namespace CareerPanda.BL.Util;

public static class UserSanitizer
{
    public static CpUser Sanitize(CpUser user)
    {
        user.Password = null;
        return user;
    }
}
