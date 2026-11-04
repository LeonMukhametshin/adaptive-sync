using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AdaptiveSync.Demo.Mvvm;
using NUnit.Framework;

namespace AdaptiveSync.Demo.EditModeTests
{
    public sealed class MvvmTests
    {
        [Test]
        public void Observable_NotifiesOnChangeOnly()
        {
            var value = new Observable<int>(1);
            var seen = new List<int>();

            var subscription = value.Subscribe(seen.Add);
            value.Value = 1;
            value.Value = 2;
            subscription.Dispose();
            value.Value = 3;

            CollectionAssert.AreEqual(new[] { 1, 2 }, seen);
        }

        [Test]
        public void Observable_SubscribeWithoutInitialValue()
        {
            var value = new Observable<string>("a");
            var seen = new List<string>();

            value.Subscribe(seen.Add, invokeNow: false);
            value.Value = "b";

            CollectionAssert.AreEqual(new[] { "b" }, seen);
        }

        [Test]
        public void ObservableList_ReplacesAndNotifies()
        {
            var list = new ObservableList<int>();
            var counts = new List<int>();
            list.Subscribe(items => counts.Add(items.Count));

            list.Replace(new[] { 1, 2, 3 });

            CollectionAssert.AreEqual(new[] { 0, 3 }, counts);
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, list.Items);
        }

        [Test]
        public void Command_RespectsCanExecute()
        {
            var allowed = new Observable<bool>(false);
            var runs = 0;
            var command = new Command(() => runs++, allowed);

            command.Execute();
            allowed.Value = true;
            command.Execute();

            Assert.AreEqual(1, runs);
        }

        [Test]
        public void AsyncCommand_BlocksReentryWhileRunning()
        {
            var context = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                AssertBlocksReentry();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(context);
            }
        }

        private static void AssertBlocksReentry()
        {
            var gate = new TaskCompletionSource<bool>();
            var runs = 0;
            var command = new AsyncCommand(() =>
            {
                runs++;
                return gate.Task;
            });

            command.Execute();
            Assert.IsTrue(command.IsRunning.Value);
            Assert.IsFalse(command.CanExecute.Value);
            command.Execute();
            Assert.AreEqual(1, runs, "A running command must not start again.");

            gate.SetResult(true);
            Assert.IsFalse(command.IsRunning.Value);
            Assert.IsTrue(command.CanExecute.Value);
        }

        [Test]
        public void CompositeDisposable_DisposesInReverseOrder()
        {
            var log = new List<string>();
            var composite = new CompositeDisposable();
            composite.Add(new Subscription(() => log.Add("first")));
            composite.Add(new Subscription(() => log.Add("second")));

            composite.Dispose();
            composite.Add(new Subscription(() => log.Add("late")));

            CollectionAssert.AreEqual(new[] { "second", "first", "late" }, log);
        }
    }
}
