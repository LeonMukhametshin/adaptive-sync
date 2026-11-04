using System;
using System.Collections.Generic;
using AdaptiveSync.Demo.App;
using NUnit.Framework;

namespace AdaptiveSync.Demo.EditModeTests
{
    public sealed class AppServicesTests
    {
        [Test]
        public void ReturnsRegisteredService()
        {
            var services = new AppServices();
            var log = new List<string>();
            var service = new Tracked("a", log);

            services.Register(service);

            Assert.AreSame(service, services.Get<Tracked>());
            Assert.IsTrue(services.Has<Tracked>());
            Assert.IsTrue(services.TryGet<Tracked>(out var found));
            Assert.AreSame(service, found);
        }

        [Test]
        public void MissingServiceExplainsThatBootWasSkipped()
        {
            var error = Assert.Throws<InvalidOperationException>(() => new AppServices().Get<Tracked>());

            StringAssert.Contains("Boot scene", error.Message);
        }

        [Test]
        public void ReplacingServiceDisposesThePreviousOne()
        {
            var services = new AppServices();
            var log = new List<string>();
            services.Register(new Tracked("old", log));

            services.Register(new Tracked("new", log));

            CollectionAssert.AreEqual(new[] { "old" }, log);
            Assert.AreEqual("new", services.Get<Tracked>().Name);
        }

        [Test]
        public void DisposeReleasesServicesInReverseOrder()
        {
            var services = new AppServices();
            var log = new List<string>();
            services.Register(new Tracked("first", log));
            services.Register(new Other("second", log));

            services.Dispose();

            CollectionAssert.AreEqual(new[] { "second", "first" }, log);
            Assert.IsFalse(services.Has<Tracked>());
            Assert.Throws<ObjectDisposedException>(() => services.Register(new Tracked("late", log)));
        }

        private class Tracked : IDisposable
        {
            private readonly List<string> _log;

            public Tracked(string name, List<string> log)
            {
                Name = name;
                _log = log;
            }

            public string Name { get; }

            public void Dispose() => _log.Add(Name);
        }

        private sealed class Other : Tracked
        {
            public Other(string name, List<string> log) : base(name, log)
            {
            }
        }
    }
}
