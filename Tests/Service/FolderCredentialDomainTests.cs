using Lemora.Service;
using Xunit;

namespace Tests.Service
{
    /// <summary>changing a folder's domain: the folder credentials "OLD\user" must follow the new domain (ACME -> ACMESPA)</summary>
    public class FolderCredentialDomainTests
    {
        [Theory]
        [InlineData("ACME\\Administrator", "ACME", "ACMESPA", "ACMESPA\\Administrator")]
        [InlineData("acme\\mario", "ACME", "ACMESPA", "ACMESPA\\mario")]   // case does not matter
        [InlineData("ACME\\Administrator", "ACME", "", "Administrator")]           // folder without domain: local user
        public void DomainPrefix_FollowsTheNewDomain(string user, string oldDomain, string newDomain, string expected)
        {
            Assert.Equal(expected, FolderCredentialService.FollowedName(user, oldDomain, newDomain));
        }

        [Theory]
        [InlineData("OTHER\\Administrator", "ACME", "ACMESPA")]       // another domain written by hand: stays
        [InlineData("Administrator", "ACME", "ACMESPA")]              // local user: stays
        [InlineData("admin@acme.local", "ACME", "ACMESPA")]           // UPN form: stays
        [InlineData("ACMESPA\\Administrator", "ACME", "ACMESPA")]     // "ACME" is only a prefix of the name, not its domain
        [InlineData("ACME\\Administrator", "ACME", "acme")]           // same domain
        [InlineData("Administrator", "", "ACMESPA")]                  // the folder had no domain before
        public void OtherNames_StayAsTheyAre(string user, string oldDomain, string newDomain)
        {
            Assert.Null(FolderCredentialService.FollowedName(user, oldDomain, newDomain));
        }
    }
}
