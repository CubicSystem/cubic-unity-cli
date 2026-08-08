using System;
using System.Collections.Generic;

namespace CubicEngine.UnityCli
{
    internal sealed class BoundedConcurrentQueue<T>
    {
        private readonly object _sync = new object();
        private readonly Queue<T> _items = new Queue<T>();
        private readonly int _capacity;

        public BoundedConcurrentQueue(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            _capacity = capacity;
        }

        public int Count
        {
            get
            {
                lock (_sync)
                {
                    return _items.Count;
                }
            }
        }

        public void Enqueue(T item)
        {
            lock (_sync)
            {
                while (_items.Count >= _capacity)
                {
                    _items.Dequeue();
                }

                _items.Enqueue(item);
            }
        }

        public List<T> Drain()
        {
            lock (_sync)
            {
                var drained = new List<T>(_items);
                _items.Clear();
                return drained;
            }
        }

        public void Clear()
        {
            lock (_sync)
            {
                _items.Clear();
            }
        }
    }
}
