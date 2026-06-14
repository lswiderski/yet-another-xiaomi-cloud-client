using YetAnotherXiaomiCloudClient;

namespace Tests
{
    public class Tests
    {
        private long _userId;
        private string _passToken;
        private string _scaleS400Name = "yunmai.scales.ms104";
        private string _region = "de";
        private string _appName = "xiaomiio";

        [SetUp]
        public void Setup()
        {
            
        }

        [Test]
        public async Task LoginWithTokenTest()
        {
            var client = new XiaomiClient(_appName);

            await client.LoginWithToken(_userId, _passToken);

            Assert.That(client.IsAuthenticated, Is.True);
        }

        [Test]
        public async Task GetModelWeightsTest()
        {
            var client = new XiaomiClient(_appName);

            await client.LoginWithToken(_userId, _passToken);

            var weigts = await client.GetModelWeights(_region, _scaleS400Name, 3);

            Assert.That(weigts.Count > 0, Is.True);
        }

        [Test]
        public async Task GetModelWeightsRawJsonTest()
        {
            var client = new XiaomiClient(_appName);


            await client.LoginWithToken(_userId, _passToken);

            var json = await client.GetModelWeightsRawJson(_region, _scaleS400Name);

            Assert.That(string.IsNullOrEmpty(json), Is.True);
        }

        [Test]
        public async Task IsTokenValidTest()
        {
            var client = new XiaomiClient(_appName);

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
