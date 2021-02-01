// thread safe pool to avoid allocations. originally from libuv2k.
// -> lock based because ConcurrentQueue/Stack/etc. allocate.
using System;
using System.Collections.Generic;

namespace Telepathy
{
    public class SafePool<T>
    {
        // objects
        readonly Stack<T> objects = new Stack<T>();

        // some types might need additional parameters in their constructor, so
        // we use a Func<T> generator
        readonly Func<T> objectGenerator;

        // constructor
        public SafePool(Func<T> objectGenerator)
        {
            this.objectGenerator = objectGenerator;
        }

        // take an element from the pool, or create a new one if empty
        public T Take()
        {
            lock (this)
            {
                return objects.Count > 0 ? objects.Pop() : objectGenerator();
            }
        }

        // return an element to the pool
        public void Return(T item)
        {
            lock (this)
            {
                objects.Push(item);
            }
        }

        // clear the pool with the disposer function applied to each object
        public void Clear()
        {
            lock (this)
            {
                objects.Clear();
            }
        }

        // count to see how many objects are in the pool. useful for tests.
        public int Count()
        {
            lock (this)
            {
                return objects.Count;
            }
        }
    }
}
