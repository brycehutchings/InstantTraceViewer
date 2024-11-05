using System.Collections;

namespace InstantTraceViewer
{
    public class ListBuilderSnapshot<TItem> : IReadOnlyList<TItem>
    {
        private IReadOnlyList<TItem[]> _blocks;
        private int _count;

        public ListBuilderSnapshot(IReadOnlyList<TItem[]> blocks, int lastBlockIndex)
        {
            _blocks = blocks;
            _count = _blocks.Take(_blocks.Count - 1).Sum(block => block.Length) + lastBlockIndex;
        }

        public TItem this[int index]
        {
            get
            {
                if (index >= _count)
                {
                    throw new IndexOutOfRangeException();
                }

                int blockSize = _blocks[0].Length;
                int blockIndex = index / blockSize;
                TItem[] block = _blocks[blockIndex];
                return block[index - blockIndex * blockSize];
            }
        }

        public int Count => _count;

        public IEnumerator<TItem> GetEnumerator()
        {
            int index = 0;
            for (int i = 0; i < _blocks.Count; i++)
            {
                TItem[] block = _blocks[i];
                for (int j = 0; j < block.Length; j++)
                {
                    if (index >= _count)
                    {
                        yield break;
                    }

                    yield return block[j];
                    index++;
                }
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// Efficiently allow appending items with the ability to create read-only snapshots without locks or worrying about builder state mutation.
    /// While the caller must lock when using the ListBuilder if Add and CreateSnapshot are called concurrently, the snapshot can be used
    /// without locks.
    /// </summary>
    /// <typeparam name="TItem"></typeparam>
    public class ListBuilder<TItem>
    {
        private readonly int _blockSize;
        private List<TItem[]> _blocks = new List<TItem[]>();
        private IReadOnlyList<TItem[]> _blocksSnapshotCopy = null;
        private int _lastBlockIndex = 0;

        public ListBuilder(int blockSize = 16 * 1024)
        {
            _blockSize = blockSize;
            _blocks.Add(new TItem[_blockSize]);
        }

        public void Add(TItem item)
        {
            TItem[] lastBlock = _blocks[_blocks.Count - 1];
            if (_lastBlockIndex == lastBlock.Length)
            {
                lastBlock = new TItem[lastBlock.Length];
                _blocks.Add(lastBlock);
                _lastBlockIndex = 0;
            }

            lastBlock[_lastBlockIndex++] = item;
        }

        public ListBuilderSnapshot<TItem> CreateSnapshot()
        {
            if (_blocksSnapshotCopy == null || _blocksSnapshotCopy.Count != _blocks.Count)
            {
                // We must make a copy of the blocks list to ensure that the snapshot doesn't need to use locks to
                // coordinate access to the blocks with the builder.
                _blocksSnapshotCopy = _blocks.ToArray();
            }

            return new ListBuilderSnapshot<TItem>(_blocksSnapshotCopy, _lastBlockIndex);
        }
    }
}
