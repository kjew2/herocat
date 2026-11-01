using HeroCat.Client.Core;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    public class BigNumberTests
    {
        [TestCase(999.0, "999")]
        [TestCase(1234.0, "1.23K")]
        [TestCase(45.6e6, "45.6M")]
        [TestCase(1.23e17, "123aa")]
        [TestCase(2.9e18, "2.90ab")]
        [TestCase(1e12, "1.00T")]
        [TestCase(999999.0, "1.00M")]
        public void Short(double v, string expected) => Assert.That(BigNumber.Short(v), Is.EqualTo(expected));
    }
}
