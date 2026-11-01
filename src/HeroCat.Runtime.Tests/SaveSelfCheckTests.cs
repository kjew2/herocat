using System.Collections.Generic;
using HeroCat.Runtime.Save;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    public class SaveSelfCheckTests
    {
        [Test]
        public void Run_OnUnstrippedBuild_IsOk()
        {
            var r = SaveSelfCheck.Run(TestContent.Db, TestContent.T0);
            Assert.That(r.Ok, Is.True, r.ToString());
        }

        [Test]
        public void Diff_ReportsDefaultedAndMissingFields()
        {
            // What a stripped setter looks like: the value is written but comes back as the default.
            var wrote = JObject.Parse("{\"Gold\":12.5,\"Slots\":[{\"Tier\":3}],\"Seed\":\"00ff\"}");
            var read = JObject.Parse("{\"Gold\":0.0,\"Slots\":[{\"Tier\":0}]}");
            var into = new List<string>();
            SaveSelfCheck.Diff(wrote, read, "State", into);
            Assert.That(into, Has.Count.EqualTo(3), string.Join("\n", into));
            Assert.That(into, Has.Some.StartsWith("State.Gold:"));
            Assert.That(into, Has.Some.StartsWith("State.Slots[0].Tier:"));
            Assert.That(into, Has.Some.StartsWith("State.Seed: missing"));
        }
    }
}
