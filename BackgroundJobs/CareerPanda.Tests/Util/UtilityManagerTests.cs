using CareerPanda.Framework.Util;
using Xunit;

namespace CareerPanda.Tests.Util;

public class UtilityManagerTests
{
    [Fact]
    public void GenerateToken_Returns_NonEmpty_String()
    {
        var key = new string('a', 32);
        var token = UtilityManager.GenerateToken(key, "user@test.com", "user-1", "1", "Admin", 60, Guid.NewGuid().ToString());
        Assert.False(string.IsNullOrWhiteSpace(token));
    }

    [Fact]
    public void GetMimeType_Returns_Pdf_For_Pdf_Files()
    {
        Assert.Equal("application/pdf", UtilityManager.GetMimeType("report.pdf"));
    }
}
