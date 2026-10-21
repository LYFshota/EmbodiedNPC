using EmbodiedNPC.Cognition;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CafeDemo.Tests
{
    public sealed class CafeSystemCognitionTransportTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void CognitiveWire_PreservesOptionalPlayerAndExcludesComputedProperties(bool hasPlayer)
        {
            var request = new CognitiveRequest
            {
                context = new CognitiveContextSnapshot
                {
                    relevant_player = hasPlayer ? new CognitivePlayerDto
                    {
                        player_entity_id = "player_01", display_name = "玩家",
                    } : null,
                },
            };
            var wire = JObject.Parse(request.Serialize());
            Assert.That(wire.SelectTokens("$..IsValid"), Is.Empty);
            Assert.That(wire["recall_identity"].Type, Is.EqualTo(JTokenType.Null));
            var player = wire["context"]["relevant_player"];
            if (hasPlayer)
            {
                Assert.That((string)player["player_entity_id"], Is.EqualTo("player_01"));
                Assert.That((string)player["display_name"], Is.EqualTo("玩家"));
            }
            else Assert.That(player.Type, Is.EqualTo(JTokenType.Null));
        }
    }
}
