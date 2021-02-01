using NUnit.Framework;

namespace Telepathy.Tests
{
    public class SafePoolTests
    {
        SafePool<string> pool;

        [SetUp]
        public void SetUp()
        {
            pool = new SafePool<string>(() => "new string");
        }

        [TearDown]
        public void TearDown()
        {
            pool = null;
        }

        [Test]
        public void TakeFromEmpty()
        {
            // taking from an empty pool should give us a completely new string
            Assert.That(pool.Take(), Is.EqualTo("new string"));
        }

        [Test]
        public void ReturnAndTake()
        {
            // returning and then taking should get the returned one, not a
            // newly generated one.
            pool.Return("returned");
            Assert.That(pool.Take(), Is.EqualTo("returned"));
        }

        [Test]
        public void Count()
        {
            Assert.That(pool.Count, Is.EqualTo(0));
            pool.Return("returned");
            Assert.That(pool.Count, Is.EqualTo(1));
        }
    }
}