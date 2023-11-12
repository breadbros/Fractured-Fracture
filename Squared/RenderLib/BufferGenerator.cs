﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Xna.Framework.Graphics;
using Squared.Task;
using Squared.Util;

using TIndex = System.UInt16;

namespace Squared.Render.Internal {
    public interface IBufferGenerator : IDisposable {
        void Reset (int frameIndex);
        void Flush ();

        int BytesAllocated { get; }
    }

    public interface IHardwareBuffer : IDisposable {
        void SetInactive ();
        bool TrySetInactive ();
        IHardwareBuffer SetActive ();
        void SetInactiveAndUnapply (GraphicsDevice device);
        IHardwareBuffer SetActiveAndApply (GraphicsDevice device);
        void GetBuffers (out VertexBuffer vb, out DynamicIndexBuffer ib);
    }

    public interface ISoftwareBuffer {
        IHardwareBuffer HardwareBuffer { get; }
        int HardwareVertexOffset { get; }
        int HardwareIndexOffset { get; }
        int Id { get; }
        unsafe void GetVertexPointer<T> (out T* buffer, out int capacity)
            where T : unmanaged;
        unsafe void GetIndexPointer (out TIndex* buffer, out int capacity);
    }

    public sealed class BufferGenerator<TVertex> : IBufferGenerator
        where TVertex : unmanaged
    {
        public static bool Tracing = false;

        protected sealed class SoftwareBufferPool {
            public sealed class BucketComparer : IComparer<SoftwareBuffer> {
                public static readonly BucketComparer Instance = new BucketComparer();

                public int Compare (SoftwareBuffer x, SoftwareBuffer y) {
                    return x.VertexCapacity - y.VertexCapacity;
                }
            }

            public sealed class Bucket : UnorderedList<SoftwareBuffer> {
                public readonly int Index;
                public readonly UnorderedList<SoftwareBuffer> AllocatedInstances = 
                    new UnorderedList<SoftwareBuffer>(256);

                public Bucket (int index)
                    : base (256) {
                    Index = index;
                }

                public override string ToString () {
                    return $"Bucket #{Index} Count={Count}";
                }

                new public void Sort () {
                    FastCLRSort(BucketComparer.Instance);
                }
            }

            public const int MaxItemAge = 30;
            public const int BucketSize = 128;
            public const int BucketCount = 16;
            public readonly Bucket[] Buckets = new Bucket[BucketCount];

            public readonly BufferGenerator<TVertex> BufferGenerator;

            public SoftwareBufferPool (BufferGenerator<TVertex> bufferGenerator) {
                BufferGenerator = bufferGenerator;
                for (int i = 0; i < Buckets.Length; i++)
                    Buckets[i] = new Bucket(i);
            }

            private int PickBucketIndex (int vertexCount, int indexCount) {
                int count = Math.Max(vertexCount, indexCount);
                int index = count / BucketSize;
                if (index >= BucketCount)
                    index = BucketCount - 1;
                return index;
            }

            public SoftwareBuffer Allocate (int vertexCount, int indexCount) {
                var bucketIndex = PickBucketIndex(vertexCount, indexCount);
                var result = AllocateFromBucket(bucketIndex, vertexCount, indexCount);
                // HACK: Search the next largest bucket, since it might have stuff in it we can use.
                // We will still return the buffer to that bucket once we're done with it
                if (result == null)
                    result = AllocateFromBucket(bucketIndex + 1, vertexCount, indexCount);
                if (result == null) {
                    if (Tracing)
                        Debug.WriteLine($"Could not find {typeof(TVertex)} x{vertexCount} I16 x{indexCount} in pool");

                    result = AllocateNew(vertexCount, indexCount, bucketIndex);
                }
                return result;
            }

            private SoftwareBuffer AllocateFromBucket (int bucketIndex, int vertexCount, int indexCount) {
                if ((bucketIndex < 0) || (bucketIndex >= BucketCount))
                    return null;

                var bucket = Buckets[bucketIndex];
                // Early out
                if (bucket.Count == 0)
                    return null;

                lock (bucket)
                using (var e = bucket.GetEnumerator())
                while (e.GetNext(out var item)) {
                    if ((item.VertexCapacity < vertexCount) || (item.IndexCapacity < indexCount))
                        continue;

                    e.RemoveCurrent();
                    bucket.AllocatedInstances.Add(item);
                    return item;
                }

                return null;
            }

            private SoftwareBuffer AllocateNew (int vertexCount, int indexCount, int bucketIndex) {
                var result = new SoftwareBuffer(BufferGenerator, vertexCount, indexCount, bucketIndex);
                var bucket = Buckets[bucketIndex];
                lock (bucket)
                    bucket.AllocatedInstances.Add(result);
                return result;
            }

            public void LockAllBuckets () {
                for (int i = 0; i < Buckets.Length; i++)
                    Monitor.Enter(Buckets[i]);
            }

            public void UnlockAllBuckets () {
                for (int i = Buckets.Length - 1; i >= 0; i--)
                    Monitor.Exit(Buckets[i]);
            }

            public void FlushLocked () {
                foreach (var bucket in Buckets)
                    foreach (var item in bucket.AllocatedInstances)
                        item.Flush();
            }

            public void ReleaseAllLocked () {
                foreach (var bucket in Buckets) {
                    foreach (var item in bucket.AllocatedInstances) {
                        item.Uninitialize();
                        bucket.Add(item);
                    }
                    bucket.AllocatedInstances.Clear();
                }
            }

            public void SortAndPurgeLocked (int currentFrameIndex) {
                foreach (var bucket in Buckets) {
                    using (var e = bucket.GetEnumerator())
                    while (e.GetNext(out var item)) {
                        var age = currentFrameIndex - item.FrameLastUsed;
                        if (age < MaxItemAge)
                            continue;

                        if (Tracing)
                            Debug.WriteLine($"Releasing {item} because it hasn't been used for {age} frames");

                        e.RemoveCurrent();
                        BufferGenerator.RenderManager.DisposeResource(item);
                    }

                    bucket.Sort();
                }
            }
        }

        public sealed unsafe class SoftwareBuffer : ISoftwareBuffer, IHardwareBuffer, IDisposable {
            private static volatile int NextId;

            public readonly BufferGenerator<TVertex> BufferGenerator;
            
            public bool IsInitialized {
                get;
                private set;
            }

            public bool IsDisposed {
                get;
                private set;
            }

            public bool IsActive {
                get;
                private set;
            }

            public bool IsFlushed {
                get;
                private set;
            }

            public int Id {
                get;
                private set;
            }

            public int HardwareVertexOffset => 0;
            public int HardwareIndexOffset => 0;

            public readonly NativeAllocation VertexAllocation;
            public readonly NativeAllocation IndexAllocation;

            public TVertex* Vertices {
                get {
                    if (!IsInitialized)
                        throw new InvalidOperationException("Buffer not initialized");
                    if (IsFlushed)
                        throw new InvalidOperationException("Vertex buffer already flushed");
                    return (TVertex*)VertexAllocation.Data;
                }
            }

            public readonly int VertexCapacity;
            public int VertexCount {
                get;
                private set;
            }

            public TIndex* Indices {
                get {
                    if (!IsInitialized)
                        throw new InvalidOperationException("Buffer not initialized");
                    if (IsFlushed)
                        throw new InvalidOperationException("Index buffer already flushed");
                    return (TIndex*)IndexAllocation.Data;
                }
            }

            public int IndexCapacity;
            public int IndexCount {
                get;
                private set;
            }

            internal DynamicVertexBuffer VertexBuffer;
            internal DynamicIndexBuffer IndexBuffer;

            public IHardwareBuffer HardwareBuffer => this;

            public readonly int BucketIndex;

            public int FrameLastUsed {
                get;
                private set;
            }

            internal SoftwareBuffer (
                BufferGenerator<TVertex> bufferGenerator,
                int vertexCount, int indexCount,
                int bucketIndex
            ) {
                Id = Interlocked.Increment(ref NextId);
                BufferGenerator = bufferGenerator;
                BucketIndex = bucketIndex;
                VertexCount = vertexCount;
                IndexCount = indexCount;
                // Ensure that buffers aren't lopsided too often. Extra indices don't cost too much, and the 'way more indices than vertices' case is rare
                VertexCapacity = vertexCount;
                IndexCapacity = Math.Max(vertexCount, indexCount);
                VertexAllocation = BufferGenerator.Allocator.Allocate(VertexCapacity * Marshal.SizeOf<TVertex>());
                IndexAllocation = BufferGenerator.Allocator.Allocate(IndexCapacity * Marshal.SizeOf<TIndex>());
            }

            public void Dispose () {
                IsDisposed = true;
                Uninitialize();
                VertexAllocation.ReleaseReference();
                IndexAllocation.ReleaseReference();
                BufferGenerator.RenderManager.DisposeResource(VertexBuffer);
                BufferGenerator.RenderManager.DisposeResource(IndexBuffer);
            }

            public void Initialize (int vertexCount, int indexCount, int frameIndex) {
                if (IsDisposed)
                    throw new ObjectDisposedException("SoftwareBuffer");
                if (IsInitialized)
                    throw new InvalidOperationException("Already initialized");
                if (vertexCount > VertexCapacity)
                    throw new ArgumentOutOfRangeException("vertexCount");
                if (indexCount > IndexCapacity)
                    throw new ArgumentOutOfRangeException("indexCount");

                VertexCount = vertexCount;
                IndexCount = indexCount;
                IsInitialized = true;
                IsFlushed = false;
                FrameLastUsed = frameIndex;
            }

            public void Uninitialize () {
                if (IsActive)
                    throw new Exception("Software buffer uninitialized while active");
                IsFlushed = IsInitialized = false;
                VertexCount = IndexCount = 0;
            }

            public void SetInactiveAndUnapply (GraphicsDevice device) {
                if (!IsInitialized)
                    throw new InvalidOperationException("Buffer not initialized");
                if (!IsActive)
                    throw new InvalidOperationException("Buffer not active");

                IsActive = false;
                device.SetVertexBuffer(null);
                device.Indices = null;
            }

            public IHardwareBuffer SetActiveAndApply (GraphicsDevice device) {
                if (IsActive)
                    throw new InvalidOperationException("Buffer already active");
                if (!IsInitialized)
                    throw new InvalidOperationException("Buffer not initialized");

                Flush();
                IsActive = true;
                device.SetVertexBuffer(VertexBuffer);
                device.Indices = IndexBuffer;

                return this;
            }

            public void SetInactive () {
                if (!IsInitialized)
                    throw new InvalidOperationException("Buffer not initialized");
                if (!IsActive)
                    throw new InvalidOperationException("Buffer not active");

                IsActive = false;
            }

            public bool TrySetInactive () {
                if (!IsActive || !IsInitialized)
                    return false;

                IsActive = false;
                return true;
            }

            public IHardwareBuffer SetActive () {
                if (IsActive || !IsInitialized)
                    return null;

                IsActive = true;
                return this;
            }

            public void GetBuffers (out VertexBuffer vb, out DynamicIndexBuffer ib) {
                if (!IsActive || !IsInitialized)
                    throw new InvalidOperationException("Buffer not active");

                Flush();
                vb = VertexBuffer;
                ib = IndexBuffer;
            }

            public void Flush () {
                if (IsFlushed)
                    return;

                IsFlushed = true;
                if (VertexBuffer == null)
                    VertexBuffer = new DynamicVertexBuffer(BufferGenerator.RenderManager.DeviceManager.Device, typeof(TVertex), VertexCapacity, BufferUsage.WriteOnly);
                if (IndexBuffer == null)
                    IndexBuffer = new DynamicIndexBuffer(BufferGenerator.RenderManager.DeviceManager.Device, IndexElementSize.SixteenBits, IndexCapacity, BufferUsage.WriteOnly);

                VertexBuffer.SetDataPointerEXT(0, (IntPtr)VertexAllocation.Data, VertexCount * Marshal.SizeOf<TVertex>(), SetDataOptions.Discard);
                IndexBuffer.SetDataPointerEXT(0, (IntPtr)IndexAllocation.Data, IndexCount * Marshal.SizeOf<TIndex>(), SetDataOptions.Discard);
            }
            public void GetVertexPointer<T> (out T* buffer, out int capacity)
                where T : unmanaged {
                if (typeof(T) != typeof(TVertex))
                    throw new ArgumentException("This buffer is of type " + typeof(TVertex));

                buffer = (T*)(void*)Vertices;
                capacity = VertexCapacity;
            }

            public void GetIndexPointer (out TIndex* buffer, out int capacity) {
                buffer = Indices;
                capacity = IndexCapacity;
            }

            public override string ToString() {
                var stateString = IsFlushed
                    ? "Flushed"
                    : (
                        IsInitialized
                            ? "Initialized"
                            : "Uninitialized"
                    );
                var vc = IsInitialized ? VertexCount : VertexCapacity;
                var ic = IsInitialized ? IndexCount : IndexCapacity;
                return $"<Buffer #{Id} {typeof(TVertex)} x{vc} I16 x{ic} ({stateString})>";
            }
        }

        object _StateLock = new object();
        private int _LastFrameReset;

        readonly SoftwareBufferPool _SoftwareBufferPool;
        readonly Dictionary<string, SoftwareBuffer> _BufferCache = new Dictionary<string, SoftwareBuffer>();

        public readonly NativeAllocator Allocator = new NativeAllocator();
        public readonly RenderManager RenderManager;
        public readonly object CreateResourceLock;

        public int BytesAllocated => Allocator.BytesInUse;

        public int DeviceId { get; private set; }

        public BufferGenerator (RenderManager renderManager) {
            if (renderManager == null)
                throw new ArgumentNullException("renderManager");

            _SoftwareBufferPool = new SoftwareBufferPool(this);
            RenderManager = renderManager;
            DeviceId = renderManager.DeviceManager.DeviceId;
            CreateResourceLock = renderManager.CreateResourceLock;
        }

        public void Dispose () {
            // FIXME
            lock (_StateLock) {
            }
        }

        public void Flush () {
            // It's important to kick off buffer uploads early so that they're likely to be ready by the time we want to draw with them.
            lock (_StateLock) {
                _SoftwareBufferPool.LockAllBuckets();
                try {
                    _SoftwareBufferPool.FlushLocked();
                } finally {
                    _SoftwareBufferPool.UnlockAllBuckets();
                }
            }
        }

        public void Reset (int frameIndex) {
            var id = RenderManager.DeviceManager.DeviceId;

            lock (_StateLock) {
                _SoftwareBufferPool.LockAllBuckets();

                try {
                    _BufferCache.Clear();
                    _SoftwareBufferPool.ReleaseAllLocked();
                    _SoftwareBufferPool.SortAndPurgeLocked(frameIndex);
                } finally {
                    _SoftwareBufferPool.UnlockAllBuckets();
                }

                _LastFrameReset = frameIndex;

                /*
                Array.Clear(_VertexArray, 0, _VertexArray.Length);
                Array.Clear(_IndexArray, 0, _IndexArray.Length);
                 */
            }
        }

        public void SetCachedBuffer (string key, SoftwareBuffer buffer) {
            lock (_BufferCache) {
                if (_BufferCache.TryGetValue(key, out var old)) {
                    if (old == buffer)
                        return;

                    // It's not safe to return this to the pool.
                    RenderManager.DisposeResource(old);
                }
                _BufferCache[key] = buffer;
            }
        }

        public SoftwareBuffer GetOrCreateCachedBuffer (string key, int vertexCount, int indexCount, out bool isNew) {
            // It's unfortunate to do all this in the lock but the alternative is worse.
            lock (_BufferCache) {
                _BufferCache.TryGetValue(key, out var result);
                if (result != null) {
                    // FIXME: Find a way to reuse it
                    if ((result.VertexCount < vertexCount) || (result.IndexCount < indexCount)) {
                        RenderManager.DisposeResource(result);
                        result = null;
                    }
                }
                if (result == null) {
                    isNew = true;
                    result = Allocate(vertexCount, indexCount);
                } else
                    isNew = false;
                _BufferCache[key] = result;
                return result;
            }
        }

        /// <summary>
        /// Allocates a software vertex/index buffer pair that you can write vertices and indices into. 
        /// Once this generator is flushed, it will have an associated hardware buffer containing your vertex/index data.
        /// </summary>
        /// <param name="vertexCount">The number of vertices.</param>
        /// <param name="indexCount">The number of indices.</param>
        /// <returns>A software buffer.</returns>
        public SoftwareBuffer Allocate (int vertexCount, int indexCount) {
            const int rounding = 16;

            var requestedVertexCount = (vertexCount + rounding - 1) / rounding * rounding;
            var requestedIndexCount = (indexCount + rounding - 1) / rounding * rounding;

            var swb = _SoftwareBufferPool.Allocate(requestedVertexCount, requestedIndexCount);
            swb.Initialize(vertexCount, indexCount, _LastFrameReset);
            return swb;
        }
    }
}
