using InstantTraceViewer.Common;

namespace InstantTraceViewerTests
{
    [TestClass]
    public class ListBuilderTests
    {
        [TestMethod]
        public void BasicTests()
        {
            var listBuilder = new ListBuilder<int>();
            var snapshot0 = listBuilder.CreateSnapshot();
            Assert.AreEqual(0, snapshot0.Count);
            listBuilder.Add(1);
            var snapshot1 = listBuilder.CreateSnapshot();
            Assert.AreEqual(0, snapshot0.Count);
            Assert.AreEqual(1, snapshot1.Count);
            CollectionAssert.AreEqual(new int[0], snapshot0.ToArray());
            CollectionAssert.AreEqual(new int[] { 1 }, snapshot1.ToArray());
        }

        [TestMethod]
        public void BasicMultiBlockTests()
        {
            var listBuilder = new ListBuilder<int>(1 /* 1 element per block */);
            var snapshot0 = listBuilder.CreateSnapshot();
            Assert.AreEqual(0, snapshot0.Count);

            listBuilder.Add(1);
            var snapshot1 = listBuilder.CreateSnapshot();
            Assert.AreEqual(0, snapshot0.Count);
            Assert.AreEqual(1, snapshot1.Count);
            CollectionAssert.AreEqual(new int[0], snapshot0.ToArray());
            CollectionAssert.AreEqual(new int[] { 1 }, snapshot1.ToArray());

            listBuilder.Add(2);
            var snapshot2 = listBuilder.CreateSnapshot();
            Assert.AreEqual(0, snapshot0.Count);
            Assert.AreEqual(1, snapshot1.Count);
            Assert.AreEqual(2, snapshot2.Count);
            CollectionAssert.AreEqual(new int[0], snapshot0.ToArray());
            CollectionAssert.AreEqual(new int[] { 1 }, snapshot1.ToArray());
            CollectionAssert.AreEqual(new int[] { 1, 2 }, snapshot2.ToArray());

            listBuilder.Add(3);
            var snapshot3 = listBuilder.CreateSnapshot();
            Assert.AreEqual(0, snapshot0.Count);
            Assert.AreEqual(1, snapshot1.Count);
            Assert.AreEqual(2, snapshot2.Count);
            Assert.AreEqual(3, snapshot3.Count);
            CollectionAssert.AreEqual(new int[0], snapshot0.ToArray());
            CollectionAssert.AreEqual(new int[] { 1 }, snapshot1.ToArray());
            CollectionAssert.AreEqual(new int[] { 1, 2 }, snapshot2.ToArray());
            CollectionAssert.AreEqual(new int[] { 1, 2, 3 }, snapshot3.ToArray());
        }

        [TestMethod]
        public void MultiBlockStressTests()
        {
            foreach (int blockSize in new[] { 1, 2, 4, 16 })
            {
                foreach (int addCount in new[] { 0, 1, 2, 3, 4, 5, 15, 16, 17, 1000 })
                {
                    foreach (int addCountAgain in new[] { 0, 1, 2, 3, 4, 5, 15, 16, 17, 1000 })
                    {
                        var listBuilder = new ListBuilder<int>(blockSize);

                        for (int i = 0; i < addCount; i++)
                        {
                            listBuilder.Add(i);
                        }

                        var expectedCollection = Enumerable.Range(0, addCount).ToArray();
                        var snapshot1 = listBuilder.CreateSnapshot();
                        Assert.AreEqual(addCount, snapshot1.Count);
                        CollectionAssert.AreEqual(expectedCollection, snapshot1.ToArray());

                        for (int i = 0; i < addCountAgain; i++)
                        {
                            listBuilder.Add(i);
                        }

                        Assert.AreEqual(addCount, snapshot1.Count);
                        CollectionAssert.AreEqual(expectedCollection, snapshot1.ToArray());

                        var expectedCollectionAgain = Enumerable.Range(0, addCountAgain).ToArray();
                        var snapshot2 = listBuilder.CreateSnapshot();
                        Assert.AreEqual(addCount + addCountAgain, snapshot2.Count);
                        CollectionAssert.AreEqual(expectedCollection.Concat(expectedCollectionAgain).ToArray(), snapshot2.ToArray());
                    }
                }
            }
        }
    }
}