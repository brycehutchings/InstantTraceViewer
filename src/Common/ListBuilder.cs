using System.Collections;

namespace InstantTraceViewer.Common
{
    public class ListBuilderSnapshot<TItem> : IReadOnlyList<TItem>
    {
        private TItem[][] _blocks;
        private int _count;

        public ListBuilderSnapshot(List<TItem[]> items, int lastBlockIndex)
        {
            _blocks = items.ToArray(); // Make copy of blocks so we don't have to worry about locking while enumerating.
            _count = _blocks.Take(_blocks.Length - 1).Sum(block => block.Length) + lastBlockIndex;
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
            for (int i = 0; i < _blocks.Length; i++)
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

    public class ListBuilder<TItem>
    {
        private List<TItem[]> _blocks = new List<TItem[]>();
        private int _lastBlockIndex = 0;

        public ListBuilder(int blockSize = 16 * 1024)
        {
            _blocks.Add(new TItem[blockSize]);
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
            return new ListBuilderSnapshot<TItem>(_blocks, _lastBlockIndex);
        }
    }
}
