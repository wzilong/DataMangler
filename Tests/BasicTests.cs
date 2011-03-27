﻿/*
The contents of this file are subject to the Mozilla Public License
Version 1.1 (the "License"); you may not use this file except in
compliance with the License. You may obtain a copy of the License at
http://www.mozilla.org/MPL/

Software distributed under the License is distributed on an "AS IS"
basis, WITHOUT WARRANTY OF ANY KIND, either express or implied. See the
License for the specific language governing rights and limitations
under the License.

The Original Code is DataMangler Key-Value Store.

The Initial Developer of the Original Code is Mozilla Corporation.

Original Author: Kevin Gadd (kevin.gadd@gmail.com)
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using NUnit.Framework;
using Squared.Data.Mangler.Serialization;
using Squared.Task;
using System.IO;
using Squared.Util;

namespace Squared.Data.Mangler.Tests {
    public class BasicTestFixture {
        public string TestFile;
        public StreamSource Storage;
        public TaskScheduler Scheduler;

        [SetUp]
        public virtual void SetUp () {
            Scheduler = new TaskScheduler();

            TestFile = Path.GetTempFileName();
            Storage = new AlternateStreamSource(TestFile);
        }

        [TearDown]
        public virtual void TearDown () {
            Scheduler.Dispose();
            File.Delete(TestFile);
        }
    }

    [TestFixture]
    public class BasicTests : BasicTestFixture {
        public Tangle<int> Tangle;

        [SetUp]
        public override void SetUp () {
            base.SetUp();

            Tangle = new Tangle<int>(
                Scheduler, Storage, 
                serializer: BlittableSerializer<int>.Serialize,
                deserializer: BlittableSerializer<int>.Deserialize,
                ownsStorage: true
            );
        }

        [TearDown]
        public override void TearDown () {
            // Tangle.ExportStreams(@"C:\dm_streams\");
            Tangle.Dispose();
            base.TearDown();
        }

        [Test]
        public void CanGetValueByNameAfterSettingIt () {
            Scheduler.WaitFor(Tangle.Set("hello", 1));
            Assert.AreEqual(1, Scheduler.WaitFor(Tangle.Get("hello")));
        }

        [Test]
        public void GetThrowsIfKeyIsNotFound () {
            try {
                Scheduler.WaitFor(Tangle.Get("missing"));
                Assert.Fail("Should have thrown");
            } catch (FutureException fe) {
                Assert.IsInstanceOf<KeyNotFoundException>(fe.InnerException);
            }
        }

        [Test]
        public void NumericKeysWork () {
            var key = new TangleKey(1234);
            Scheduler.WaitFor(Tangle.Set(key, 1));
            Assert.AreEqual(1, Scheduler.WaitFor(Tangle.Get(key)));
        }

        [Test]
        public void CanOverwriteExistingValueBySettingItAgain () {
            Scheduler.WaitFor(Tangle.Set("hello", 1));
            Scheduler.WaitFor(Tangle.Set("hello", 3));
            Assert.AreEqual(3, Scheduler.WaitFor(Tangle.Get("hello")));
        }

        [Test]
        public void AddReturnsFalseInsteadOfOverwriting () {
            Assert.AreEqual(true, Scheduler.WaitFor(Tangle.Add("hello", 1)));
            Assert.AreEqual(false, Scheduler.WaitFor(Tangle.Add("hello", 3)));
            Assert.AreEqual(1, Scheduler.WaitFor(Tangle.Get("hello")));
        }

        [Test]
        public void FindReturnsReferenceThatCanBeUsedToFetchValue () {
            Scheduler.WaitFor(Tangle.Set("a", 1));
            Scheduler.WaitFor(Tangle.Set("b", 2));

            var itemRef = Scheduler.WaitFor(Tangle.Find("a"));
            Assert.AreEqual("a", itemRef.Key.ToString());
            Assert.AreEqual(1, Scheduler.WaitFor(itemRef.GetValue()));
        }

        [Test]
        public void FindReturnsReferenceThatCanBeUsedToReplaceValue () {
            Scheduler.WaitFor(Tangle.Set("a", 1));
            Scheduler.WaitFor(Tangle.Set("b", 2));

            var itemRef = Scheduler.WaitFor(Tangle.Find("a"));
            Scheduler.WaitFor(itemRef.SetValue(3));
        }

        [Test]
        public void FindThrowsIfKeyIsNotFound () {
            try {
                Scheduler.WaitFor(Tangle.Find("missing"));
                Assert.Fail("Should have thrown");
            } catch (FutureException fe) {
                Assert.IsInstanceOf<KeyNotFoundException>(fe.InnerException);
            }
        }

        [Test]
        public void AddOrUpdateInvokesCallbackWhenKeyIsFound () {
            Scheduler.WaitFor(Tangle.Add("a", 1));
            Scheduler.WaitFor(Tangle.AddOrUpdate("a", 999, (oldValue) => oldValue + 1));
            Scheduler.WaitFor(Tangle.AddOrUpdate("b", 128, (oldValue) => oldValue + 1));

            Assert.AreEqual(2, Scheduler.WaitFor(Tangle.Get("a")));
            Assert.AreEqual(128, Scheduler.WaitFor(Tangle.Get("b")));
        }

        [Test]
        public void AddOrUpdateCallbackCanAbortUpdate () {
            Scheduler.WaitFor(Tangle.Add("a", 1));
            Scheduler.WaitFor(Tangle.AddOrUpdate("a", 999, (ref int value) => false));

            Assert.AreEqual(1, Scheduler.WaitFor(Tangle.Get("a")));
        }

        [Test]
        public void AddOrUpdateCallbackCanMutateValue () {
            Scheduler.WaitFor(Tangle.Add("a", 1));
            Scheduler.WaitFor(Tangle.AddOrUpdate("a", 999, (ref int value) => {
                value += 1;
                return true;
            }));

            Assert.AreEqual(2, Scheduler.WaitFor(Tangle.Get("a")));
        }

        [Test]
        public void InsertInSequentialOrder () {
            Scheduler.WaitFor(Tangle.Set("aa", 4));
            Scheduler.WaitFor(Tangle.Set("ea", 3));
            Scheduler.WaitFor(Tangle.Set("qa", 2));
            Scheduler.WaitFor(Tangle.Set("za", 1));

            Assert.AreEqual(
                new object[] { "aa", "ea", "qa", "za" }, (from k in Tangle.Keys select k.Value).ToArray()
            );
        }

        [Test]
        public void InsertInReverseOrder () {
            Scheduler.WaitFor(Tangle.Set("za", 4));
            Scheduler.WaitFor(Tangle.Set("qa", 3));
            Scheduler.WaitFor(Tangle.Set("ea", 2));
            Scheduler.WaitFor(Tangle.Set("aa", 1));

            Assert.AreEqual(
                new object[] { "aa", "ea", "qa", "za" }, (from k in Tangle.Keys select k.Value).ToArray()
            );
        }

        protected IEnumerator<object> WriteLotsOfValues (Tangle<int> tangle, int numIterations, int direction) {
            if (direction > 0)
                for (int i = 0; i < numIterations; i++) {
                    yield return tangle.Set(i, i);
                }
            else
                for (int i = numIterations - 1; i >= 0; i--) {
                    yield return tangle.Set(i, i);
                }
        }

        [Test]
        public void CanWriteLotsOfValuesSequentially () {
            const int numValues = 50000;

            long startTime = Time.Ticks;
            Scheduler.WaitFor(WriteLotsOfValues(Tangle, numValues, 1));
            decimal elapsedSeconds = (decimal)(Time.Ticks - startTime) / Time.SecondInTicks;
            Console.WriteLine(
                "Wrote {0} values in ~{1:00.000} second(s) at ~{2:00000.00} values/sec.",
                numValues, elapsedSeconds, numValues / elapsedSeconds
            );

            startTime = Time.Ticks;
            Scheduler.WaitFor(CheckLotsOfValues(Tangle, numValues));
            elapsedSeconds = (decimal)(Time.Ticks - startTime) / Time.SecondInTicks;
            Console.WriteLine(
                "Read {0} values in ~{1:00.000} second(s) at ~{2:00000.00} values/sec.",
                numValues, elapsedSeconds, numValues / elapsedSeconds
            );
        }

        [Test]
        public void CanWriteLotsOfValuesInReverse () {
            const int numValues = 500000;

            long startTime = Time.Ticks;
            Scheduler.WaitFor(WriteLotsOfValues(Tangle, numValues, -1));
            decimal elapsedSeconds = (decimal)(Time.Ticks - startTime) / Time.SecondInTicks;
            Console.WriteLine(
                "Wrote {0} values in ~{1:00.000} second(s) at ~{2:00000.00} values/sec.",
                numValues, elapsedSeconds, numValues / elapsedSeconds
            );

            startTime = Time.Ticks;
            Scheduler.WaitFor(CheckLotsOfValues(Tangle, numValues));
            elapsedSeconds = (decimal)(Time.Ticks - startTime) / Time.SecondInTicks;
            Console.WriteLine(
                "Read {0} values in ~{1:00.000} second(s) at ~{2:00000.00} values/sec.",
                numValues, elapsedSeconds, numValues / elapsedSeconds
            );
        }

        protected IEnumerator<object> WriteLotsOfValuesInBatch (Tangle<int> tangle, int numIterations, int direction) {
            int batchSize = 256;
            Batch<int> batch = null;

            if (direction > 0)
                for (int i = 0; i < numIterations; i++) {
                    if (batch == null)
                        batch = new Batch<int>(batchSize);

                    batch.Add(i, i);

                    if (batch.Count == batchSize) {
                        yield return batch.Execute(Tangle);
                        batch = null;
                    }
                }
            else
                for (int i = numIterations - 1; i >= 0; i--) {
                    if (batch == null)
                        batch = new Batch<int>(batchSize);

                    batch.Add(i, i);

                    if (batch.Count == batchSize) {
                        yield return batch.Execute(Tangle);
                        batch = null;
                    }
                }

            if (batch != null)
                yield return batch.Execute(Tangle);
        }

        [Test]
        public void BatchValuesInReverse () {
            const int numValues = 500000;

            long startTime = Time.Ticks;
            Scheduler.WaitFor(WriteLotsOfValuesInBatch(Tangle, numValues, -1));
            decimal elapsedSeconds = (decimal)(Time.Ticks - startTime) / Time.SecondInTicks;
            Console.WriteLine(
                "Wrote {0} values in ~{1:00.000} second(s) at ~{2:00000.00} values/sec.",
                numValues, elapsedSeconds, numValues / elapsedSeconds
            );

            startTime = Time.Ticks;
            Scheduler.WaitFor(CheckLotsOfValues(Tangle, numValues));
            elapsedSeconds = (decimal)(Time.Ticks - startTime) / Time.SecondInTicks;
            Console.WriteLine(
                "Read {0} values in ~{1:00.000} second(s) at ~{2:00000.00} values/sec.",
                numValues, elapsedSeconds, numValues / elapsedSeconds
            );
        }

        protected IEnumerator<object> CheckLotsOfValues (Tangle<int> tangle, int numIterations) {
            for (int i = 0; i < numIterations; i++) {
                var f = tangle.Get(i);
                yield return f;
                Assert.AreEqual(i, f.Result);
            }
        }

        [Test]
        public void TestCount () {
            Assert.AreEqual(0, Tangle.Count);
            Scheduler.WaitFor(Tangle.Add(1, 1));
            Assert.AreEqual(1, Tangle.Count);
            Scheduler.WaitFor(Tangle.Add(2, 2));
            Assert.AreEqual(2, Tangle.Count);
            Scheduler.WaitFor(Tangle.Add(2, 2));
            Assert.AreEqual(2, Tangle.Count);
            Scheduler.WaitFor(Tangle.Set(1, 3));
            Assert.AreEqual(2, Tangle.Count);
        }

        [Test]
        public void TestMultiGet () {
            const int numValues = 500000;

            Scheduler.WaitFor(WriteLotsOfValuesInBatch(Tangle, numValues, -1));

            var keys = new List<int>();
            for (int i = 0; i < numValues; i += 2)
                keys.Add(i);

            long startTime = Time.Ticks;
            var fMultiGet = Tangle.Select(keys);
            var results = Scheduler.WaitFor(fMultiGet);
            decimal elapsedSeconds = (decimal)(Time.Ticks - startTime) / Time.SecondInTicks;
            Console.WriteLine(
                "Fetched {0} values in ~{1:00.000} second(s) at ~{2:00000.00} values/sec.",
                keys.Count, elapsedSeconds, keys.Count / elapsedSeconds
            );

            Assert.AreEqual(keys.Count, results.Count());

            Assert.AreEqual(
                keys.OrderBy((k) => k)
                    .ToArray(), 
                results.OrderBy((kvp) => kvp.Key)
                    .Select((kvp) => kvp.Key)
                    .ToArray()
            );

            foreach (var kvp in results)
                Assert.AreEqual((int)kvp.Key, kvp.Value);
        }

        [Test]
        public void TestMultiGetMissingValuesStillProduceAKeyValuePair () {
            var fMultiGet = Tangle.Select(new[] { 1, 2 });
            var results = Scheduler.WaitFor(fMultiGet);

            Assert.AreEqual(1, results[0].Key);
            Assert.AreEqual(2, results[1].Key);
            Assert.AreEqual(default(int), results[0].Value);
            Assert.AreEqual(default(int), results[1].Value);
        }

        [Test]
        public void TestGetAllValues () {
            const int numValues = 100000;

            Scheduler.WaitFor(WriteLotsOfValuesInBatch(Tangle, numValues, -1));

            long startTime = Time.Ticks;
            var fValues = Tangle.GetAllValues();
            var values = Scheduler.WaitFor(fValues);
            decimal elapsedSeconds = (decimal)(Time.Ticks - startTime) / Time.SecondInTicks;
            Console.WriteLine(
                "Fetched {0} values in ~{1:00.000} second(s) at ~{2:00000.00} values/sec.",
                numValues, elapsedSeconds, numValues / elapsedSeconds
            );

            Assert.AreEqual(numValues, values.Length);
            Assert.AreEqual(
                Enumerable.Range(0, numValues).ToArray(),
                values.OrderBy((v) => v).ToArray()
            );
        }

        [Test]
        public void TestJoin () {
            for (int i = 0; i < 8; i++)
                Scheduler.WaitFor(Tangle.Set(i, i * 2));

            using (var otherTangle = new Tangle<int>(Scheduler, new SubStreamSource(Storage, "2_", false))) {
                var keys = new List<string>();
                for (int i = 0; i < 8; i++) {
                    var key = new String((char)('a' + i), 1);
                    keys.Add(key);
                    Scheduler.WaitFor(otherTangle.Set(key, i));
                }

                var joinResult = Scheduler.WaitFor(
                    otherTangle.Join(
                        Tangle, keys,
                        (string leftKey, ref int leftValue) => 
                            leftValue,
                        (string leftKey, ref int leftValue, int rightKey, ref int rightValue) =>
                            new { leftKey, leftValue, rightKey, rightValue }
                    )
                );

                for (int i = 0; i < 8; i++) {
                    var item = joinResult[i];
                    Assert.AreEqual(keys[i], item.leftKey);
                    Assert.AreEqual(i, item.leftValue);
                    Assert.AreEqual(i, item.rightKey);
                    Assert.AreEqual(i * 2, item.rightValue);
                }
            }
        }

        [Test]
        public void TestSimpleJoin () {
            for (int i = 0; i < 8; i++)
                Scheduler.WaitFor(Tangle.Set(i, i * 2));

            using (var otherTangle = new Tangle<int>(Scheduler, new SubStreamSource(Storage, "2_", false))) {
                var keys = new List<string>();
                for (int i = 0; i < 8; i++) {
                    var key = new String((char)('a' + i), 1);
                    keys.Add(key);
                    Scheduler.WaitFor(otherTangle.Set(key, i));
                }

                var joinResult = Scheduler.WaitFor(
                    otherTangle.Join(
                        Tangle, keys,
                        (leftValue) => leftValue
                    )
                );

                for (int i = 0; i < 8; i++) {
                    var item = joinResult[i];
                    Assert.AreEqual(i, item.Key);
                    Assert.AreEqual(i * 2, item.Value);
                }
            }
        }
    }

    [TestFixture]
    public class SynchronizationTests : BasicTestFixture {
        public Tangle<int> Tangle;

        [SetUp]
        public override void SetUp () {
            base.SetUp();

            Tangle = new Tangle<int>(
                Scheduler, Storage,
                serializer: BlittableSerializer<int>.Serialize,
                deserializer: BlittableSerializer<int>.Deserialize,
                ownsStorage: true
            );
        }

        [TearDown]
        public override void TearDown () {
            Tangle.Dispose();
            base.TearDown();
        }

        [Test]
        public void BarrierPreventsOperationsLaterInTheQueueFromCompleting () {
            var barrier = Tangle.CreateBarrier();
            var fOperation = Tangle.Add(1, 1);
            Scheduler.WaitFor(barrier);
            Assert.AreEqual(0, Tangle.Count);
            barrier.Open();
            Scheduler.WaitFor(fOperation);
            Assert.AreEqual(1, Tangle.Count);
        }

        [Test]
        public void DisposingOperationFutureCancelsTheOperation () {
            var barrier1 = Tangle.CreateBarrier(false);
            var fOperation = Tangle.Add(1, 1);
            var barrier2 = Tangle.CreateBarrier(true);
            fOperation.Dispose();
            barrier1.Open();
            Scheduler.WaitFor(barrier2);
            Assert.AreEqual(0, Tangle.Count);
        }

        [Test]
        public void DisposingTangleFailsPendingOperations () {
            var barrier = Tangle.CreateBarrier(false);
            var fOperation = Tangle.Add(1, 1);
            Scheduler.WaitFor(barrier);
            Tangle.Dispose();

            Assert.Throws<FutureDisposedException>(
                () => Scheduler.WaitFor(fOperation)
            );
        }

        [Test]
        public void BarrierCollectionOpensAllContainedBarriersWhenOpened () {
            var bc = new BarrierCollection(false, Tangle, Tangle);
            var fOperation = Tangle.Add(1, 1);

            Scheduler.WaitFor(bc);
            Assert.IsFalse(fOperation.Completed);
            bc.Open();
            Scheduler.WaitFor(fOperation);
            Assert.AreEqual(1, Tangle.Count);
        }
    }

    [TestFixture]
    public class StringTests : BasicTestFixture {
        public Tangle<string> Tangle;

        [SetUp]
        public unsafe override void SetUp () {
            base.SetUp();

            var serializer = new Squared.Data.Mangler.Serialization.StringSerializer(
                Encoding.UTF8
            );

            Tangle = new Tangle<string>(
                Scheduler, Storage,
                serializer: serializer.Serialize, 
                deserializer: serializer.Deserialize,
                ownsStorage: true
            );
        }

        [TearDown]
        public override void TearDown () {
            // Tangle.ExportStreams(@"C:\dm_streams\");
            Tangle.Dispose();
            base.TearDown();
        }

        [Test]
        public void OverwritingWithShorterStringWorks () {
            Scheduler.WaitFor(Tangle.Set("hello", "long string"));
            Assert.AreEqual("long string", Scheduler.WaitFor(Tangle.Get("hello")));
            Scheduler.WaitFor(Tangle.Set("hello", "world"));
            Assert.AreEqual("world", Scheduler.WaitFor(Tangle.Get("hello")));
        }

        [Test]
        public void OverwritingWithLongerStringWorks () {
            Scheduler.WaitFor(Tangle.Set("hello", "world"));
            Assert.AreEqual("world", Scheduler.WaitFor(Tangle.Get("hello")));
            Scheduler.WaitFor(Tangle.Set("hello", "long string"));
            Assert.AreEqual("long string", Scheduler.WaitFor(Tangle.Get("hello")));
        }

        [Test]
        public void UpdateGrowthWorks () {
            var s = "a";
            Scheduler.WaitFor(Tangle.Set("test", s));

            Tangle<string>.UpdateCallback callback = (str) => str + "a";

            for (int i = 0; i < 10; i++) {
                s = s + "a";

                Scheduler.WaitFor(Tangle.AddOrUpdate("test", null, callback));

                Assert.AreEqual(s, Scheduler.WaitFor(Tangle.Get("test")));
            }
        }

        [Test]
        public void UpdateShrinkageWorks () {
            var s = new String('a', 11);
            Scheduler.WaitFor(Tangle.Set("test", s));

            Tangle<string>.UpdateCallback callback = (str) => str.Substring(0, str.Length - 1);

            for (int i = 0; i < 10; i++) {
                s = s.Substring(0, s.Length - 1);

                Scheduler.WaitFor(Tangle.AddOrUpdate("test", null, callback));

                Assert.AreEqual(s, Scheduler.WaitFor(Tangle.Get("test")));
            }
        }

        [Test]
        public void TestWastedDataBytes () {
            Assert.AreEqual(0, Tangle.WastedDataBytes);
            Scheduler.WaitFor(Tangle.Set(1, "abcd"));
            Assert.AreEqual(0, Tangle.WastedDataBytes);
            Scheduler.WaitFor(Tangle.Set(1, "abcdefgh"));
            Assert.AreEqual(4, Tangle.WastedDataBytes);
            Scheduler.WaitFor(Tangle.Set(1, "abc"));
            Assert.AreEqual(4, Tangle.WastedDataBytes);
            Scheduler.WaitFor(Tangle.Set(1, "abcdefgh"));
            Assert.AreEqual(4, Tangle.WastedDataBytes);
        }

        [Test]
        public void TestStoringHugeValue () {
            var hugeString = new String('a', 1024 * 1024 * 32);
            Scheduler.WaitFor(Tangle.Set(1, hugeString));
            Assert.AreEqual(hugeString, Scheduler.WaitFor(Tangle.Get(1)));
        }

        [Test]
        public void TestZeroByteValue () {
            var emptyString = "";
            Scheduler.WaitFor(Tangle.Set(1, emptyString));
            Assert.AreEqual(emptyString, Scheduler.WaitFor(Tangle.Get(1)));
        }

        [Test]
        public void TestGrowFromZeroBytes () {
            string emptyString = "", largerString = "abcdefgh";
            Scheduler.WaitFor(Tangle.Set(1, emptyString));
            Assert.AreEqual(emptyString, Scheduler.WaitFor(Tangle.Get(1)));
            Scheduler.WaitFor(Tangle.Set(1, largerString));
            Assert.AreEqual(largerString, Scheduler.WaitFor(Tangle.Get(1)));
            Assert.AreEqual(0, Tangle.WastedDataBytes);
        }
    }

    [TestFixture]
    public class KeyTests {
        [Test]
        public void TestKeyEquals () {
            var keyA = new TangleKey("abcd");
            var keyB = new TangleKey("abcd");
            var keyC = new TangleKey(2);
            var keyD = new TangleKey(2);

            Assert.IsTrue(keyA.Equals(keyA));
            Assert.IsTrue(keyA.Equals(keyB));
            Assert.IsTrue(keyC.Equals(keyC));
            Assert.IsTrue(keyC.Equals(keyD));
            Assert.IsFalse(keyA.Equals(keyC));
        }
    }
}
