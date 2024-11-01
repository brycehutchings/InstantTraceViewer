using InstantTraceViewer;

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
            CompareCollections(new int[0], snapshot0);
            listBuilder.Add(1);
            var snapshot1 = listBuilder.CreateSnapshot();
            CompareCollections(new int[0], snapshot0);
            CompareCollections(new int[] { 1 }, snapshot1);
        }

        [TestMethod]
        public void BasicMultiBlockTests()
        {
            var listBuilder = new ListBuilder<int>(1 /* 1 element per block */);
            var snapshot0 = listBuilder.CreateSnapshot();
            Assert.AreEqual(0, snapshot0.Count);

            listBuilder.Add(1);
            var snapshot1 = listBuilder.CreateSnapshot();
            CompareCollections(new int[0], snapshot0);
            CompareCollections(new int[] { 1 }, snapshot1);

            listBuilder.Add(2);
            var snapshot2 = listBuilder.CreateSnapshot();
            CompareCollections(new int[0], snapshot0);
            CompareCollections(new int[] { 1 }, snapshot1);
            CompareCollections(new int[] { 1, 2 }, snapshot2);

            listBuilder.Add(3);
            var snapshot3 = listBuilder.CreateSnapshot();
            CompareCollections(new int[0], snapshot0);
            CompareCollections(new int[] { 1 }, snapshot1);
            CompareCollections(new int[] { 1, 2 }, snapshot2);
            CompareCollections(new int[] { 1, 2, 3 }, snapshot3);
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
                        CompareCollections(expectedCollection, snapshot1);

                        for (int i = 0; i < addCountAgain; i++)
                        {
                            listBuilder.Add(i);
                        }

                        CompareCollections(expectedCollection, snapshot1);

                        var expectedCollectionAgain = Enumerable.Range(0, addCountAgain).ToArray();
                        var snapshot2 = listBuilder.CreateSnapshot();
                        CompareCollections(expectedCollection.Concat(expectedCollectionAgain).ToArray(), snapshot2);
                    }
                }
            }
        }

        private void CompareCollections(int[] expectedCollection, IReadOnlyList<int> actualCollection)
        {
            // Test count.
            Assert.AreEqual(expectedCollection.Length, actualCollection.Count);

            // Test index[] operator.
            for (int i = 0; i < expectedCollection.Length; i++)
            {
                Assert.AreEqual(expectedCollection[i], actualCollection[i]);
            }

            // Test enumerable.
            CollectionAssert.AreEqual(expectedCollection, actualCollection.ToArray());
        }
    }
}