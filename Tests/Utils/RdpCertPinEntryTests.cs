using Lemora.Service;
using Xunit;

namespace Tests.Utils
{
    /// <summary>
    /// RDP certificate remembered per saved entry: two PCs of different customers with the same private address must not
    /// raise "certificate changed" at every switch; a real change on the same entry must still ask
    /// </summary>
    public class RdpCertPinEntryTests
    {
        private const string A = "aaaa", B = "bbbb";

        [Fact] public void Entry_SameCertificate_Ok() => Assert.Equal(RdpCertPin.PinAction.Ok, RdpCertPin.Decide(A, B, true, A));

        [Fact] public void Entry_CertificateChanged_Asks() => Assert.Equal(RdpCertPin.PinAction.AskChanged, RdpCertPin.Decide(A, A, true, B));

        // entry never pinned, the address had the same certificate (old data): taken over without asking
        [Fact] public void Entry_OldAddressPinSame_Adopted() => Assert.Equal(RdpCertPin.PinAction.Adopt, RdpCertPin.Decide(null, A, true, A));

        // entry never pinned, the address had another certificate: asked once, explaining it may be another PC with the same address
        [Fact] public void Entry_OldAddressPinDifferent_AsksOtherPc() => Assert.Equal(RdpCertPin.PinAction.AskOtherPcSameAddress, RdpCertPin.Decide(null, A, true, B));

        [Fact] public void Entry_Nothing_First() => Assert.Equal(RdpCertPin.PinAction.First, RdpCertPin.Decide(null, null, true, A));

        // quick connection: per address, as before
        [Fact] public void Quick_Changed_Asks() => Assert.Equal(RdpCertPin.PinAction.AskChanged, RdpCertPin.Decide(A, A, false, B));

        [Fact] public void Quick_Nothing_First() => Assert.Equal(RdpCertPin.PinAction.First, RdpCertPin.Decide(null, null, false, A));

        [Fact] public void Keys_EntryAndAddressDiffer() => Assert.NotEqual(RdpCertPin.EntryKey("id1"), RdpCertPin.Key("192.0.2.1", 3389));
    }
}
