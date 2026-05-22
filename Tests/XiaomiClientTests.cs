using YetAnotherXiaomiCloudClient;

namespace Tests
{
    public class Tests
    {
        private long _userId;
        private string _passToken;
        [SetUp]
        public void Setup()
        {
           
        }

        [Test]
        public async Task ConnectTest()
        {
            var client = new XiaomiClient("xiaomiio");

          
            await client.LoginWithToken(_userId, _passToken);

            var weigts = await client.GetModelWeights("de", "yunmai.scales.ms104");

            Assert.Pass();
        }
    }
}
