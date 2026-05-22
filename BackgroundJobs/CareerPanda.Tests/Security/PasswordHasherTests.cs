using CareerPanda.Framework.Security;
using Xunit;

namespace CareerPanda.Tests.Security;

public class PasswordHasherTests
{
    private readonly PasswordHasher _hasher = new();

    [Fact]
    public void Hash_And_Verify_Succeeds()
    {
        var hash = _hasher.Hash("MySecretPassword!");
        Assert.True(_hasher.Verify("MySecretPassword!", hash));
        Assert.False(_hasher.Verify("WrongPassword", hash));
    }

    [Fact]
    public void Hash_Produces_Versioned_Format()
    {
        var hash = _hasher.Hash("test");
        Assert.StartsWith("v1.", hash);
    }
}
