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

            var weigts = await client.GetModelWeights("de", "yunmai.scales.ms104", 3);

            Assert.That(weigts.Count > 0, Is.True);
        }

        [Test]
        public async Task IsTokenValidTest()
        {
            var client = new XiaomiClient("xiaomiio");

            var isValid = await client.IsTokenValid(_userId, _passToken);

            Assert.That(isValid, Is.True);
        }


        [Test]
        public async Task AuthorizationTest()
        {
            var authorization = new XiaomiClientAuthorization();


            var result = await authorization.LoginAsync();


            Assert.That(authorization.PassToken, !Is.Null);
           // Assert.That(result, Is.True);
        }

        
    }
}
