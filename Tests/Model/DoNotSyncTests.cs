using Lemora.Model.Protocol;
using Newtonsoft.Json;
using Xunit;

namespace Tests.Model
{
    /// <summary>a connection's "do not sync" flag is saved with the connection and copied by Update (edit from the editor)</summary>
    public class DoNotSyncTests
    {
        [Fact]
        public void Flag_IsOffByDefault_SurvivesJson_AndIsCopiedByUpdate()
        {
            var a = new RDP { DisplayName = "home", Address = "10.0.0.5", Port = "3389" };
            Assert.False(a.DoNotSync);

            a.DoNotSync = true;
            var json = JsonConvert.SerializeObject(a);
            var back = JsonConvert.DeserializeObject<RDP>(json)!;
            Assert.True(back.DoNotSync);

            var b = new RDP { DisplayName = "x", Address = "1.1.1.1", Port = "3389" };
            Assert.True(b.Update(a));
            Assert.True(b.DoNotSync);
        }
    }
}
